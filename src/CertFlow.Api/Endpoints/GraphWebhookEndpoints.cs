using Azure.Messaging.ServiceBus;
using CertFlow.Contracts.Models;
using CertFlow.Infrastructure.Messaging;
using Microsoft.Graph;
using System.Text.Json;

namespace CertFlow.Api.Endpoints;

public static class GraphWebhookEndpoints
{
    public static void MapGraphWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Graph sends a validation token on subscription creation — must echo it back
        app.MapPost("/api/emails/graph-webhook", async (HttpContext ctx,
            ServiceBusPublisher publisher,
            GraphServiceClient graph,
            IConfiguration config,
            CancellationToken ct) =>
        {
            // Subscription validation handshake
            if (ctx.Request.Query.TryGetValue("validationToken", out var token))
                return Results.Content(token.ToString(), "text/plain");

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var payload = JsonDocument.Parse(body);

            foreach (var notification in payload.RootElement.GetProperty("value").EnumerateArray())
            {
                var resourceData = notification.GetProperty("resourceData");
                var messageId = resourceData.GetProperty("id").GetString()!;
                var mailboxId = config["MailboxEmail"]!;

                // Fetch full email from Graph
                var message = await graph.Users[mailboxId].Messages[messageId]
                    .GetAsync(req =>
                    {
                        req.QueryParameters.Select = ["id", "subject", "body", "from", "receivedDateTime"];
                    }, ct);

                if (message is null) continue;

                var senderEmail = message.From?.EmailAddress?.Address ?? string.Empty;
                var subject = message.Subject ?? string.Empty;

                // Route: if subject contains [REF:...] it's a reply
                var isReply = System.Text.RegularExpressions.Regex.IsMatch(subject, @"\[REF:[A-Za-z0-9\-_]+\]");
                var queueName = isReply ? "email-replies" : "inbound-emails";

                var emailMsg = new EmailMessage(
                    MessageId: messageId,
                    SenderEmail: senderEmail,
                    Subject: subject,
                    Body: message.Body?.Content ?? string.Empty,
                    ReceivedAt: message.ReceivedDateTime ?? DateTimeOffset.UtcNow,
                    InReplyToMessageId: null);

                await publisher.PublishAsync(queueName, emailMsg, ct);
            }

            return Results.Accepted();
        });
    }
}
