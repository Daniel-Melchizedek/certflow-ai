using Azure.Messaging.ServiceBus;
using CertFlow.Agent;
using CertFlow.Application.Interfaces;
using CertFlow.Application.Services;
using CertFlow.Contracts.Models;
using CertFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CertFlow.Worker.Consumers;

public class InboundEmailConsumer(
    ServiceBusClient sbClient,
    AgentOrchestrator orchestrator,
    McpToolExecutor mcpTools,
    IServiceScopeFactory scopeFactory,
    CorrelationTokenService tokenService,
    ILogger<InboundEmailConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var processor = sbClient.CreateProcessor("inbound-emails");
        processor.ProcessMessageAsync += HandleAsync;
        processor.ProcessErrorAsync += e =>
        {
            logger.LogError(e.Exception, "Service Bus error on inbound-emails");
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync(ct);
        await Task.Delay(Timeout.Infinite, ct);
        await processor.StopProcessingAsync();
    }

    private async Task HandleAsync(ProcessMessageEventArgs args)
    {
        var email = JsonSerializer.Deserialize<EmailMessage>(args.Message.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        logger.LogInformation("Processing inbound email from {Sender}", email.SenderEmail);

        using var scope = scopeFactory.CreateScope();
        var requestRepo = scope.ServiceProvider.GetRequiredService<IRescheduleRequestRepository>();
        var ct = args.CancellationToken;

        // Claim before any work. Service Bus delivers at least once, and every branch below
        // ends in an email — without this a redelivery answers the candidate a second time,
        // which is exactly what happened with the duplicate "need more info" replies. Claiming
        // first also means a redelivery costs no agent calls at all.
        var messageKey = tokenService.BuildMessageKey(email.SenderEmail, email.MessageId);
        if (!await requestRepo.TryClaimInboundMessageAsync(messageKey, email.SenderEmail, email.MessageId, ct))
        {
            logger.LogInformation("Inbound email {MessageId} was already handled — dropping redelivery.",
                email.MessageId);
            await args.CompleteMessageAsync(args.Message, ct);
            return;
        }

        try
        {
            await ProcessAsync(email, scope.ServiceProvider, ct);
        }
        catch (Exception ex)
        {
            // Release the claim so a genuine failure can still be retried on redelivery,
            // rather than being permanently swallowed as a duplicate.
            logger.LogError(ex, "Processing failed for {MessageId} — releasing claim for retry.", email.MessageId);
            await requestRepo.ReleaseInboundClaimAsync(messageKey, CancellationToken.None);
            throw;
        }

        await args.CompleteMessageAsync(args.Message, ct);
    }

    private async Task ProcessAsync(EmailMessage email, IServiceProvider sp, CancellationToken ct)
    {
        var requestRepo = sp.GetRequiredService<IRescheduleRequestRepository>();
        var emailSender = sp.GetRequiredService<IEmailSender>();
        var auditRepo = sp.GetRequiredService<IAuditRepository>();
        var slotRepo = sp.GetRequiredService<ISlotRepository>();
        var apptRepo = sp.GetRequiredService<IAppointmentRepository>();
        var composer = sp.GetRequiredService<RescheduleEmailComposer>();

        // Agent 1 — extract intent
        var intent = await orchestrator.RunIntentAgentAsync(email, ct);
        if (intent is null || intent.IsAmbiguous || (intent.AppointmentId is null && !intent.IsBulk))
        {
            // Completing silently here is indistinguishable from an outage: the candidate wrote
            // to a support address and nothing ever came back. Always answer, even if the only
            // thing we can say is that we need more detail.
            logger.LogInformation("Intent unusable for {Sender} — replying to ask for detail.", email.SenderEmail);
            await emailSender.ReplyAsync(email.MessageId,
                composer.NeedMoreInfo(intent?.DisplayName ?? "there", intent?.ClarificationNeeded), ct);
            return;
        }

        if (intent.IsBulk)
        {
            await ProcessBulkAsync(email, intent, sp, ct);
            return;
        }

        // Agent 2 — validate policy and propose slots
        var policy = await orchestrator.RunPolicyAgentAsync(intent, ct);
        if (policy is null)
        {
            logger.LogWarning("Policy agent returned null for {Sender}", email.SenderEmail);
            await emailSender.ReplyAsync(email.MessageId,
                composer.NeedMoreInfo(intent.DisplayName, null), ct);
            return;
        }

        var correlationToken = tokenService.Generate();
        var idempotencyKey = tokenService.BuildIdempotencyKey(
            intent.EntraUserId, intent.AppointmentId!.Value, email.MessageId);

        var (proposals, isFallback) = await ResolveProposalsAsync(policy, intent, slotRepo, apptRepo, ct);

        var request = new RescheduleRequest
        {
            Id = Guid.NewGuid(),
            AppointmentId = intent.AppointmentId!.Value,
            CandidateEntraUserId = intent.EntraUserId,
            Reason = intent.Reason,
            Status = proposals.Count > 0 ? RescheduleRequestStatus.ProposalSent : RescheduleRequestStatus.Rejected,
            ProposedSlotIds = proposals.Select(s => s.SlotId).ToList(),
            CorrelationToken = correlationToken,
            IdempotencyKey = idempotencyKey,
            AgentReasoning = policy.AgentReasoning,
            Channel = "Email",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(48)
        };

        try
        {
            await requestRepo.AddAsync(request, ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException?.Message.Contains("IX_RescheduleRequests_IdempotencyKey") == true)
        {
            logger.LogWarning("Duplicate idempotency key {Key} — request already recorded.", idempotencyKey);
            return;
        }

        string body;
        if (proposals.Count > 0)
        {
            body = await composer.ProposalAsync(intent.DisplayName, proposals, correlationToken, isFallback, ct);
        }
        else if (!policy.IsEligible)
        {
            body = composer.Rejection(intent.DisplayName, policy.RejectionReason, correlationToken);
        }
        else
        {
            body = composer.NoAvailability(intent.DisplayName, intent.ExamCode, intent.PreferredCity,
                intent.PreferredFromDate, intent.PreferredToDate, correlationToken);
        }

        await emailSender.ReplyAsync(email.MessageId, body, ct);

        await auditRepo.LogAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationToken,
            EventType = proposals.Count > 0 ? "ProposalSent" : "RequestRejected",
            ActorType = "Agent",
            CandidateEntraUserId = intent.EntraUserId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// The candidate named several exams in one mail. Each one still goes through the policy
    /// agent on its own — eligibility and availability are per-exam questions — but the results
    /// are gathered into a single session so one email carries every option and one reply can
    /// settle all of them.
    /// </summary>
    private async Task ProcessBulkAsync(
        EmailMessage email, IntentResult intent, IServiceProvider sp, CancellationToken ct)
    {
        var emailSender = sp.GetRequiredService<IEmailSender>();
        var auditRepo = sp.GetRequiredService<IAuditRepository>();
        var slotRepo = sp.GetRequiredService<ISlotRepository>();
        var apptRepo = sp.GetRequiredService<IAppointmentRepository>();
        var composer = sp.GetRequiredService<RescheduleEmailComposer>();

        var appointmentIds = intent.AppointmentIds ?? [];
        logger.LogInformation("Bulk reschedule for {Sender}: {Count} exams", email.SenderEmail, appointmentIds.Count);

        var examProposals = new List<BulkExamProposal>();
        var toolExams = new List<object>();

        // The policy agent reasons about one exam at a time and cannot see what was offered to
        // the others, so two exams being moved into the same city and window are routinely
        // offered the identical slot as their option 1. "Option 1 for all" then commits the
        // first and fails the second on a slot that was never really free for it.
        var offeredSlotIds = new HashSet<Guid>();

        foreach (var appointmentId in appointmentIds)
        {
            var appointment = await apptRepo.GetByIdAsync(appointmentId, ct);
            if (appointment is null)
            {
                logger.LogWarning("Bulk request referenced unknown appointment {AppointmentId} — skipping.", appointmentId);
                continue;
            }

            var programme = appointment.Voucher.ExamProgram;

            // The policy agent only ever reasons about one exam, so each is presented to it as
            // an ordinary single-exam intent. The city defaults to where that exam is currently
            // booked — the exams can be in different cities, and the candidate rarely restates
            // a city per exam when asking to move all of them.
            var single = intent with
            {
                AppointmentId = appointmentId,
                ExamCode = programme.Code,
                PreferredCity = string.IsNullOrWhiteSpace(intent.PreferredCity)
                    ? appointment.Slot.TestCenter.City
                    : intent.PreferredCity,
                IsBulk = false,
                AppointmentIds = null
            };

            var policy = await orchestrator.RunPolicyAgentAsync(single, ct);
            var (proposals, isFallback) = policy is null
                ? ((IReadOnlyList<SlotProposal>)[], false)
                : await ResolveProposalsAsync(policy, single, slotRepo, apptRepo, ct);

            proposals = ClaimUnofferedSlots(proposals, offeredSlotIds);

            // Scarce cities are the normal case for a multi-exam move: the agent hands back the
            // same three Mumbai seats for every exam, the first exam claims them, and the rest
            // would be told "no availability" while seats plainly exist next month. Widen the
            // search for anything left empty, skipping what is already spoken for.
            if (proposals.Count == 0 && policy?.IsEligible == true)
            {
                var backfill = await FindNextAvailableAsync(slotRepo, apptRepo, single, offeredSlotIds, ct);
                proposals = ClaimUnofferedSlots(backfill, offeredSlotIds);
                isFallback = proposals.Count > 0;
            }

            examProposals.Add(new BulkExamProposal(
                appointmentId,
                programme.Code,
                programme.Name,
                appointment.Slot.StartUtc,
                appointment.Slot.TestCenter.IanaTimeZone,
                appointment.Slot.TestCenter.City,
                proposals,
                isFallback));

            toolExams.Add(new
            {
                appointmentId = appointmentId.ToString(),
                proposedSlotIds = proposals.Select(s => s.SlotId.ToString()).ToArray(),
                agentReasoning = policy?.AgentReasoning
            });
        }

        if (examProposals.Count == 0)
        {
            await emailSender.ReplyAsync(email.MessageId,
                composer.NeedMoreInfo(intent.DisplayName,
                    "We could not find the exams you asked about. Please reply naming the exam codes you would like to move."),
                ct);
            return;
        }

        // Session creation goes through the MCP server rather than straight to the repository:
        // it is the same write tool a future agent would call, so the bookkeeping has one
        // implementation regardless of who drives it. No model round trip is spent on it —
        // there is no judgement in allocating a token.
        var toolArgs = JsonSerializer.Serialize(new
        {
            entraUserId = intent.EntraUserId,
            displayName = intent.DisplayName,
            sourceMessageId = email.MessageId,
            examsJson = JsonSerializer.Serialize(toolExams)
        });

        var toolResult = await mcpTools.ExecuteAsync("create_bulk_reschedule_session", toolArgs, ct);
        var sessionToken = ReadSessionToken(toolResult);
        if (sessionToken is null)
        {
            logger.LogError("create_bulk_reschedule_session returned no token: {Result}", toolResult);
            throw new InvalidOperationException($"Bulk session creation failed: {toolResult}");
        }

        var body = await composer.BulkProposalAsync(intent.DisplayName, examProposals, sessionToken, ct);
        await emailSender.ReplyAsync(email.MessageId, body, ct);

        await auditRepo.LogAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = sessionToken,
            EventType = "BulkProposalSent",
            ActorType = "Agent",
            CandidateEntraUserId = intent.EntraUserId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);
    }

    private static string? ReadSessionToken(string toolResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            return doc.RootElement.TryGetProperty("sessionToken", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The policy agent only searches the window the candidate named. When that window is
    /// genuinely empty the candidate still needs something actionable, so widen the search
    /// rather than replying with a bare refusal.
    /// </summary>
    private static async Task<(IReadOnlyList<SlotProposal> Proposals, bool IsFallback)> ResolveProposalsAsync(
        PolicyResult policy,
        IntentResult intent,
        ISlotRepository slotRepo,
        IAppointmentRepository apptRepo,
        CancellationToken ct)
    {
        var proposals = Dedupe(policy.ProposedSlots ?? []);

        if (policy.IsEligible && proposals.Count == 0)
        {
            proposals = await FindNextAvailableAsync(slotRepo, apptRepo, intent, null, ct);
            return (proposals, proposals.Count > 0);
        }

        if (proposals.Count > 0)
            return (proposals, RescheduleEmailComposer.FallsOutsideRequestedWindow(
                proposals, intent.PreferredFromDate, intent.PreferredToDate));

        return (proposals, false);
    }

    /// <summary>
    /// The policy agent sometimes previews the same slot twice and returns it twice, which
    /// reaches the candidate as two identical options under different numbers — one of which
    /// cannot be booked once the other is taken. Ranks are reassigned after the de-duplication
    /// so the printed list stays 1..N with no gap.
    /// </summary>
    /// <summary>
    /// Keeps only the slots no earlier exam in this same bulk proposal has already been offered,
    /// re-numbering what survives so each exam still reads 1..N. Offering one seat to two exams
    /// is offering it to neither: whichever commits second is refused, and the candidate is told
    /// a slot they were shown moments earlier is gone.
    /// </summary>
    private static IReadOnlyList<SlotProposal> ClaimUnofferedSlots(
        IReadOnlyList<SlotProposal> slots, HashSet<Guid> alreadyOffered)
    {
        var kept = new List<SlotProposal>();
        foreach (var s in slots.OrderBy(x => x.Rank))
            if (alreadyOffered.Add(s.SlotId))
                kept.Add(s with { Rank = kept.Count + 1 });
        return kept;
    }

    private static IReadOnlyList<SlotProposal> Dedupe(IReadOnlyList<SlotProposal> slots) =>
        [.. slots.DistinctBy(s => s.SlotId)
            .OrderBy(s => s.Rank)
            .Select((s, i) => s with { Rank = i + 1 })];

    /// <summary>
    /// Widest-net search used when the candidate's requested window has no seats. Honours the
    /// stated time-of-day first and only drops it if that leaves nothing, so an "afternoons
    /// please" request still gets afternoons wherever they exist.
    /// </summary>
    private static async Task<IReadOnlyList<SlotProposal>> FindNextAvailableAsync(
        ISlotRepository slotRepo,
        IAppointmentRepository apptRepo,
        IntentResult intent,
        IReadOnlySet<Guid>? exclude,
        CancellationToken ct)
    {
        var appointment = await apptRepo.GetByIdAsync(intent.AppointmentId!.Value, ct);
        var city = !string.IsNullOrWhiteSpace(intent.PreferredCity)
            ? intent.PreferredCity
            : appointment?.Slot.TestCenter.City;

        if (string.IsNullOrWhiteSpace(city)) return [];

        var from = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));
        var to = from.AddMonths(6);

        var slots = await slotRepo.SearchAvailableAsync(city, from, to, null, intent.PreferredTimeOfDay, ct);
        if (slots.Count == 0)
            slots = await slotRepo.SearchAvailableAsync(city, from, to, null, null, ct);

        return [.. slots.Where(s => exclude is null || !exclude.Contains(s.Id))
            .OrderBy(s => s.StartUtc)
            .Take(3)
            .Select((s, i) => new SlotProposal(s.Id, s.StartUtc, s.DurationMinutes,
                s.TestCenter.Name, s.TestCenter.City, i + 1))];
    }
}
