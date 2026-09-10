using Azure.Messaging.ServiceBus;
using CertFlow.Agent;
using CertFlow.Application.Handlers;
using CertFlow.Application.Interfaces;
using CertFlow.Application.Services;
using CertFlow.Contracts.Commands;
using CertFlow.Contracts.Models;
using CertFlow.Domain.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CertFlow.Worker.Consumers;

public class EmailReplyConsumer(
    ServiceBusClient sbClient,
    AgentOrchestrator orchestrator,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailReplyConsumer> logger) : BackgroundService
{
    private static readonly Regex TokenRegex = new(@"\[REF:([A-Za-z0-9\-_]+)\]", RegexOptions.Compiled);
    // "option" written out, anywhere in the sentence ("I'll take option 2", "yes, option 3
    // please"). Tried first, because an explicit label outranks a leading bare "yes". The
    // keyword is required here so a number buried in prose is never read as a selection.
    private static readonly Regex ExplicitOptionRegex =
        new(@"\boption\s*([123])\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A reply that opens with the choice and then trails off into pleasantries
    // ("1", "Option 1 please, thanks!", "yes please"). Trailing prose has to be allowed:
    // anchoring the whole line rejected everything but a bare number. The lookahead stops a
    // leading date such as "2 October works for me" from being taken as option 2.
    private static readonly Regex LeadingChoiceRegex =
        new(@"^\s*(?:option\s*)?([123]|yes)\b(?!\s*(?:st|nd|rd|th)\b|[\s/\-]*(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec|\d))",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Cut points for the quoted thread, matched against the raw HTML. Outlook emits
    // "1 <hr><div id=divRplyFwdMsg>From: ...", so the boundary is only visible as markup —
    // once tags are flattened the answer and the quote share a line and cannot be separated.
    private static readonly string[] HtmlQuoteMarkers =
    [
        "<div id=\"divRplyFwdMsg\"",
        "<div id='divRplyFwdMsg'",
        "<div id=\"appendonsend\"",
        "<blockquote",
        "<hr",
        "-----Original Message-----",
    ];

    // Backstop for plain-text replies, where no markup boundary exists.
    private static readonly Regex TextQuoteMarker =
        new(@"^\s*(From:|On .+ wrote:|_{10,}|-{5,}\s*Original Message)",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var processor = sbClient.CreateProcessor("email-replies");
        processor.ProcessMessageAsync += HandleAsync;
        processor.ProcessErrorAsync += e =>
        {
            logger.LogError(e.Exception, "Service Bus error on email-replies");
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

        var fullText = HtmlToText(email.Body);

        // Replies thread without a rewritten subject, so the token arrives inside the quoted
        // body. The first match is the newest proposal — Outlook stacks quotes newest-first.
        var tokenMatch = TokenRegex.Match(email.Subject);
        if (!tokenMatch.Success) tokenMatch = TokenRegex.Match(fullText);
        if (!tokenMatch.Success)
        {
            logger.LogWarning("Reply email missing correlation token in subject or body: {Subject}", email.Subject);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        var token = tokenMatch.Groups[1].Value;

        using var scope = scopeFactory.CreateScope();
        var requestRepo = scope.ServiceProvider.GetRequiredService<IRescheduleRequestRepository>();
        var bulkRepo = scope.ServiceProvider.GetRequiredService<IBulkRescheduleRepository>();
        var handler = scope.ServiceProvider.GetRequiredService<ConfirmRescheduleHandler>();

        // A bulk proposal's token belongs to the session, not to any one exam, so that lookup
        // has to come first — the child requests carry their own tokens which are never mailed.
        var bulkSession = await bulkRepo.GetByTokenAsync(token, args.CancellationToken);
        if (bulkSession is not null)
        {
            await HandleBulkReplyAsync(email, bulkSession, scope.ServiceProvider, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        var request = await requestRepo.GetByCorrelationTokenAsync(token, args.CancellationToken);
        if (request is null)
        {
            logger.LogWarning("No reschedule request found for token {Token}", token);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        // Graph retries notifications and can deliver duplicates, and candidates often send a
        // second "thanks" reply. Neither should be retried five times into the dead-letter
        // queue — the first confirmation already did the work. Checked before parsing so a
        // trailing "thanks" on a settled request never triggers a clarification email.
        if (request.Status is not RescheduleRequestStatus.ProposalSent
                          and not RescheduleRequestStatus.Pending)
        {
            logger.LogInformation("Request {RequestId} already in state {Status} — ignoring duplicate reply.",
                request.Id, request.Status);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        // Only the text above the quote markers is the candidate's own writing. Scanning the
        // whole body would read our own proposal back and book a slot the candidate never chose.
        var candidateText = CandidateReplyText(email.Body);
        var choiceMatch = ExplicitOptionRegex.Match(candidateText);
        if (!choiceMatch.Success) choiceMatch = LeadingChoiceRegex.Match(candidateText);

        var slotIndex = -1;
        if (choiceMatch.Success)
        {
            var choice = choiceMatch.Groups[1].Value.Trim().ToUpperInvariant();
            slotIndex = choice switch { "2" => 1, "3" => 2, _ => 0 };
        }

        // Either no choice could be read, or they picked an option that was never offered —
        // easy to do when a proposal came back with fewer than three slots. Both cases get the
        // same answer, because dropping the mail leaves the candidate waiting on nothing.
        if (slotIndex < 0 || slotIndex >= request.ProposedSlotIds.Count)
        {
            // Logged with the extracted text so a parsing miss is diagnosable from logs alone,
            // rather than needing the raw message pulled back out of the mailbox.
            logger.LogInformation(
                "Reply for {RequestId} had no usable choice (index {Index} of {Count}) in {Text} — asking to clarify.",
                request.Id, slotIndex, request.ProposedSlotIds.Count,
                candidateText.Length > 200 ? candidateText[..200] : candidateText);

            var composer = scope.ServiceProvider.GetRequiredService<RescheduleEmailComposer>();
            var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            var proposals = request.ProposedSlotIds
                .Select((id, i) => new SlotProposal(id, default, 0, string.Empty, string.Empty, i + 1))
                .ToList();
            await emailSender.ReplyAsync(email.MessageId,
                composer.Clarification("there", proposals, token),
                args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        var selectedSlot = request.ProposedSlotIds[slotIndex];
        await handler.HandleAsync(
            new ConfirmRescheduleCommand(request.Id, selectedSlot, "Email", email.SenderEmail),
            args.CancellationToken);

        logger.LogInformation("Reschedule request {RequestId} committed via email reply.", request.Id);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    /// <summary>
    /// A reply to a multi-exam proposal. The choices are handed to the Confirmation Agent
    /// rather than parsed here: candidates answer in prose ("option 1 for all", "the October
    /// 6th one for Azure"), which no practical regex covers. The agent commits each choice
    /// through the MCP write tool, and that tool re-checks policy and that the slot was
    /// actually offered — so a misread reply cannot book something that was never proposed.
    /// </summary>
    private async Task HandleBulkReplyAsync(
        EmailMessage email, BulkRescheduleSession session, IServiceProvider sp, CancellationToken ct)
    {
        var bulkRepo = sp.GetRequiredService<IBulkRescheduleRepository>();
        var slotRepo = sp.GetRequiredService<ISlotRepository>();
        var emailSender = sp.GetRequiredService<IEmailSender>();
        var composer = sp.GetRequiredService<RescheduleEmailComposer>();
        var auditRepo = sp.GetRequiredService<IAuditRepository>();

        if (session.Status == BulkSessionStatus.Committed)
        {
            logger.LogInformation("Bulk session {SessionId} already committed — ignoring duplicate reply.", session.Id);
            return;
        }

        // Only exams still awaiting a choice go to the agent. On a follow-up reply after a
        // partial commit, re-offering the settled ones would invite the candidate to book
        // them twice.
        var open = session.ChildRequests
            .Where(r => r.Status is RescheduleRequestStatus.ProposalSent or RescheduleRequestStatus.Pending)
            .Where(r => r.ProposedSlotIds.Count > 0)
            // Must match the order BulkProposalAsync printed the exams in, because the email
            // invites a positional reply ("1, 2, 1") that is meaningless if the two disagree.
            .OrderBy(r => r.Appointment.Voucher.ExamProgram.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (open.Count == 0)
        {
            logger.LogInformation("Bulk session {SessionId} has no exams awaiting a choice.", session.Id);
            return;
        }

        var exams = new List<object>();
        foreach (var child in open)
        {
            var options = new List<object>();
            var rank = 1;
            foreach (var slotId in child.ProposedSlotIds)
            {
                var slot = await slotRepo.GetByIdAsync(slotId, ct);
                if (slot is null) continue;

                var local = RescheduleEmailComposer.ToCentreLocal(slot.StartUtc, slot.TestCenter.IanaTimeZone);
                options.Add(new
                {
                    option = rank++,
                    slotId = slotId.ToString(),
                    startLocal = local.ToString("dddd, dd MMMM yyyy 'at' h:mm tt"),
                    timeZone = slot.TestCenter.IanaTimeZone,
                    testCenter = slot.TestCenter.Name,
                    city = slot.TestCenter.City
                });
            }

            if (options.Count == 0) continue;

            exams.Add(new
            {
                rescheduleRequestId = child.Id.ToString(),
                examCode = child.Appointment.Voucher.ExamProgram.Code,
                examName = child.Appointment.Voucher.ExamProgram.Name,
                options
            });
        }

        var candidateText = CandidateReplyText(email.Body);
        var payload = JsonSerializer.Serialize(new { exams, notifyEmail = email.SenderEmail });

        var result = await orchestrator.RunConfirmationAgentAsync(candidateText, payload, ct);

        var outcomes = result?.Outcomes ?? [];
        if (outcomes.Count == 0)
        {
            logger.LogInformation(
                "Bulk reply for session {SessionId} matched no exam in {Text} — asking to clarify.",
                session.Id, candidateText.Length > 200 ? candidateText[..200] : candidateText);

            var codes = open.Select(r => r.Appointment.Voucher.ExamProgram.Code).ToList();
            await emailSender.ReplyAsync(email.MessageId,
                composer.BulkClarification(session.DisplayName, codes, session.CorrelationToken), ct);
            return;
        }

        // What the candidate is told comes from the database, not from the agent's account of
        // itself. The agent can misreport a refused commit as a success, and a confirmation
        // that wrongly says an exam moved is the one failure a candidate cannot recover from.
        // Its outcomes are used only for the wording of a failure the database already agrees
        // happened.
        var fresh = await bulkRepo.GetByTokenFreshAsync(session.CorrelationToken, ct);
        var reconciled = Reconcile(fresh, open, outcomes);

        var settled = reconciled.Count(o => o.Committed);
        session.Status = settled == session.ChildRequests.Count
            ? BulkSessionStatus.Committed
            : settled > 0 ? BulkSessionStatus.PartiallyCommitted : BulkSessionStatus.ProposalSent;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await bulkRepo.SaveChangesAsync(ct);

        await emailSender.ReplyAsync(email.MessageId,
            composer.BulkConfirmation(session.DisplayName, reconciled), ct);

        await auditRepo.LogAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = session.CorrelationToken,
            EventType = session.Status == BulkSessionStatus.Committed
                ? "BulkRescheduleCommitted"
                : "BulkReschedulePartiallyCommitted",
            ActorType = "Candidate",
            CandidateEntraUserId = session.CandidateEntraUserId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);

        logger.LogInformation("Bulk session {SessionId}: {Settled} of {Total} exams committed.",
            session.Id, settled, session.ChildRequests.Count);
    }

    /// <summary>
    /// Turns the post-commit database state into the outcomes the confirmation email prints.
    /// A committed exam's appointment now points at its new slot, so the times come from there.
    /// The agent's reported reason is reused only to explain exams the database shows as still
    /// uncommitted; if it offered none, a neutral fallback is used.
    /// </summary>
    private static List<ExamOutcome> Reconcile(
        BulkRescheduleSession? fresh,
        List<RescheduleRequest> attempted,
        IReadOnlyList<ExamOutcome> agentOutcomes)
    {
        var results = new List<ExamOutcome>();

        foreach (var original in attempted)
        {
            var examCode = original.Appointment.Voucher.ExamProgram.Code;
            var current = fresh?.ChildRequests.FirstOrDefault(r => r.Id == original.Id);
            var claimed = agentOutcomes.FirstOrDefault(o =>
                string.Equals(o.ExamCode, examCode, StringComparison.OrdinalIgnoreCase));

            // Not mentioned by the agent and not committed — the candidate did not choose for
            // this exam, so it stays open and is left out of the receipt entirely.
            if (claimed is null && current?.Status != RescheduleRequestStatus.Committed)
                continue;

            if (current?.Status == RescheduleRequestStatus.Committed)
            {
                var slot = current.Appointment.Slot;
                var local = RescheduleEmailComposer.ToCentreLocal(slot.StartUtc, slot.TestCenter.IanaTimeZone);
                results.Add(new ExamOutcome(
                    examCode,
                    Committed: true,
                    NewStartLocal: local.ToString("yyyy-MM-ddTHH:mm:ss"),
                    TimeZone: slot.TestCenter.IanaTimeZone,
                    TestCenter: slot.TestCenter.Name,
                    City: slot.TestCenter.City,
                    OrderNumber: current.Appointment.OrderNumber,
                    FailReason: null));
            }
            else
            {
                results.Add(new ExamOutcome(
                    examCode,
                    Committed: false,
                    NewStartLocal: null, TimeZone: null, TestCenter: null, City: null, OrderNumber: null,
                    FailReason: string.IsNullOrWhiteSpace(claimed?.FailReason)
                        ? "we could not complete this change — the slot may have been taken"
                        : claimed.FailReason));
            }
        }

        return results;
    }

    /// <summary>
    /// Block-level tags become newlines before the rest are stripped. Outlook sends a reply as
    /// one unbroken run of markup, so a naive tag-to-space strip collapses the whole message
    /// onto a single line and a bare "2" can never match as its own line.
    /// </summary>
    private static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li|/tr|/h[1-6])\s*[^>]*>", "\n",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]*>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"[ \t]+", " ");
    }

    /// <summary>
    /// Returns only what the candidate typed, with the quoted thread removed. Cutting on the raw
    /// HTML first is essential: the quote boundary is an &lt;hr&gt; or a reply-header div, and
    /// both flatten to a space, which would leave the answer on the same line as our own
    /// "Reply with 1, 2 or 3" text and let a stale number be read back as a choice.
    /// </summary>
    private static string CandidateReplyText(string html)
    {
        var cut = html.Length;
        foreach (var marker in HtmlQuoteMarkers)
        {
            var i = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i < cut) cut = i;
        }

        var text = HtmlToText(html[..cut]);

        var textMarker = TextQuoteMarker.Match(text);
        return textMarker.Success ? text[..textMarker.Index] : text;
    }
}
