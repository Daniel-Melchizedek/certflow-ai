using Azure.Messaging.ServiceBus;
using CertFlow.Agent;
using CertFlow.Application.Interfaces;
using CertFlow.Application.Services;
using CertFlow.Contracts.Models;
using CertFlow.Domain.Entities;
using CertFlow.Infrastructure.Email;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CertFlow.Worker.Consumers;

public class InboundEmailConsumer(
    ServiceBusClient sbClient,
    AgentOrchestrator orchestrator,
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
        var email = JsonSerializer.Deserialize<EmailMessage>(args.Message.Body)!;
        logger.LogInformation("Processing inbound email from {Sender}", email.SenderEmail);

        // Agent 1 — extract intent
        var intent = await orchestrator.RunIntentAgentAsync(email, args.CancellationToken);
        if (intent is null || intent.IsAmbiguous)
        {
            logger.LogWarning("Intent agent returned null or ambiguous for {Sender}", email.SenderEmail);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        // Agent 2 — validate policy and propose slots
        var policy = await orchestrator.RunPolicyAgentAsync(intent, args.CancellationToken);
        if (policy is null)
        {
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var requestRepo = scope.ServiceProvider.GetRequiredService<IRescheduleRequestRepository>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

        var correlationToken = tokenService.Generate();
        var idempotencyKey = tokenService.BuildIdempotencyKey(intent.EntraUserId, intent.AppointmentId);

        var request = new RescheduleRequest
        {
            Id = Guid.NewGuid(),
            AppointmentId = intent.AppointmentId,
            CandidateEntraUserId = intent.EntraUserId,
            Reason = intent.Reason,
            Status = policy.IsEligible ? RescheduleRequestStatus.ProposalSent : RescheduleRequestStatus.Rejected,
            ProposedSlotIds = policy.ProposedSlots.Select(s => s.SlotId).ToList(),
            CorrelationToken = correlationToken,
            IdempotencyKey = idempotencyKey,
            AgentReasoning = policy.AgentReasoning,
            Channel = "Email",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(48)
        };

        await requestRepo.AddAsync(request, args.CancellationToken);

        if (policy.IsEligible && policy.ProposedSlots.Count > 0)
        {
            var body = BuildProposalEmail(intent.DisplayName, policy.ProposedSlots, correlationToken);
            var subject = $"Your {intent.ExamCode} rescheduling options [REF:{correlationToken}]";
            await emailSender.SendAsync(email.SenderEmail, subject, body, args.CancellationToken);
        }
        else
        {
            var subject = $"RE: Your {intent.ExamCode} reschedule request [REF:{correlationToken}]";
            var body = $"<p>Hi {intent.DisplayName},</p><p>Unfortunately we cannot reschedule: {policy.RejectionReason}</p>";
            await emailSender.SendAsync(email.SenderEmail, subject, body, args.CancellationToken);
        }

        await auditRepo.LogAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationToken,
            EventType = policy.IsEligible ? "ProposalSent" : "RequestRejected",
            ActorType = "Agent",
            CandidateEntraUserId = intent.EntraUserId,
            Timestamp = DateTimeOffset.UtcNow
        }, args.CancellationToken);

        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private static string BuildProposalEmail(string name, IEnumerable<CertFlow.Contracts.Models.SlotProposal> slots, string token)
    {
        var options = string.Join("\n", slots.Select(s =>
            $"<li><strong>Option {s.Rank}:</strong> {s.StartUtc:dddd, dd MMM yyyy} at {s.StartUtc:HH:mm} UTC — {s.TestCenterName}, {s.TestCenterCity}</li>"));

        return $"""
            <p>Hi {name},</p>
            <p>Here are available slots matching your request. Rescheduling is always <strong>free of charge</strong>.</p>
            <p>Reply with <strong>1</strong>, <strong>2</strong>, or <strong>3</strong> to confirm your choice:</p>
            <ol>{options}</ol>
            <p>This offer expires in 48 hours. Reference: [REF:{token}]</p>
            """;
    }
}
