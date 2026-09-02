using CertFlow.Application.Interfaces;
using CertFlow.Contracts.Dtos;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CertFlow.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/graph-subscription/create", async (
            GraphServiceClient graph,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var mailboxId = config["MailboxEmail"]!;
            var webhookUrl = $"{config["WebhookBaseUrl"]}/api/emails/graph-webhook";

            // Check for existing subscription
            var existing = await graph.Subscriptions.GetAsync(cancellationToken: ct);
            var active = existing?.Value?.FirstOrDefault(s =>
                s.NotificationUrl == webhookUrl && s.ExpirationDateTime > DateTimeOffset.UtcNow);
            if (active is not null)
                return Results.Ok(new { subscriptionId = active.Id, message = "Already active" });

            var sub = await graph.Subscriptions.PostAsync(new Subscription
            {
                ChangeType = "created",
                NotificationUrl = webhookUrl,
                Resource = $"users/{mailboxId}/mailFolders/Inbox/messages",
                ExpirationDateTime = DateTimeOffset.UtcNow.AddDays(3),
                ClientState = config["GraphClientState"] ?? "certflow-webhook-secret"
            }, cancellationToken: ct);

            return Results.Ok(new { subscriptionId = sub!.Id });
        });

        app.MapPost("/admin/agents/register", async (
            CertFlow.Agent.AgentRegistrationService agentReg,
            CancellationToken ct) =>
        {
            await agentReg.RegisterAllAsync(ct);
            return Results.Ok(new { message = "Agents registered" });
        });

        app.MapGet("/admin/audit", async (
            IAuditRepository audit,
            string? candidateId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? correlationId,
            CancellationToken ct) =>
        {
            var events = await audit.QueryAsync(candidateId, from, to, correlationId, ct);
            return Results.Ok(events.Select(e => new AuditEventDto(
                e.Id, e.CorrelationId, e.EventType, e.ActorType,
                e.CandidateEntraUserId, e.Payload, e.Timestamp)));
        });

        app.MapGet("/admin/requests", async (
            IRescheduleRequestRepository requests,
            IAppointmentRepository appointments,
            CancellationToken ct) =>
        {
            // Return placeholder — full impl queries DB directly
            return Results.Ok(new { message = "Request list endpoint" });
        });
    }
}
