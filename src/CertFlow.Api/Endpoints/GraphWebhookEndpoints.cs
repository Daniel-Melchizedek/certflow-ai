using CertFlow.Contracts.Models;
using CertFlow.Infrastructure.Messaging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Text.Json;

namespace CertFlow.Api.Endpoints;

public static class GraphWebhookEndpoints
{
    /// <summary>
    /// Queue of raw Graph notifications. The webhook only parks notifications here; the
    /// Worker does the Graph lookups.
    /// </summary>
    public const string NotificationQueue = "notifications";

    public static void MapGraphWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Graph treats a notification as delivered only if it gets a 2xx within 3 seconds,
        // and marks endpoints that miss that budget "slow" and then "drop" — at which point
        // notifications are discarded unrecoverably. So this handler does no Graph I/O: it
        // checks clientState, parks the notification on a queue, and returns 202. Fetching
        // the message and validating the sender happen in the Worker.
        app.MapPost("/api/emails/graph-webhook", async (HttpContext ctx,
            ServiceBusPublisher publisher,
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("GraphWebhook");

            // Subscription validation handshake
            if (ctx.Request.Query.TryGetValue("validationToken", out var token))
                return Results.Content(token.ToString(), "text/plain");

            using var reader = new StreamReader(ctx.Request.Body);
            var payload = JsonDocument.Parse(await reader.ReadToEndAsync(ct));
            var expectedClientState = config["GraphClientState"] ?? "certflow-webhook-secret";

            foreach (var notification in payload.RootElement.GetProperty("value").EnumerateArray())
            {
                // This endpoint is anonymous, so clientState is the only thing proving the
                // callback came from our subscription rather than an arbitrary poster.
                var clientState = notification.TryGetProperty("clientState", out var cs)
                    ? cs.GetString()
                    : null;
                if (!string.Equals(clientState, expectedClientState, StringComparison.Ordinal))
                {
                    log.LogWarning("Dropping notification with mismatched clientState.");
                    continue;
                }

                if (!notification.TryGetProperty("resourceData", out var resourceData)
                    || !resourceData.TryGetProperty("id", out var idProp))
                {
                    log.LogWarning("Dropping notification with no resourceData.id.");
                    continue;
                }

                await publisher.PublishAsync(NotificationQueue,
                    new GraphNotification(idProp.GetString()!,
                        notification.TryGetProperty("subscriptionId", out var s) ? s.GetString() : null),
                    ct);
            }

            return Results.Accepted();
        });

        MapLifecycleEndpoint(app);
    }

    /// <summary>
    /// Graph posts subscription-state events here rather than to the change-notification URL.
    /// Ignoring them is what silently breaks a webhook pipeline days later: the subscription
    /// lapses or is removed and mail simply stops arriving with no error anywhere.
    /// </summary>
    private static void MapLifecycleEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/emails/graph-lifecycle", async (HttpContext ctx,
            GraphServiceClient graph,
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("GraphLifecycle");

            // Graph validates this URL the same way as the notification URL.
            if (ctx.Request.Query.TryGetValue("validationToken", out var token))
                return Results.Content(token.ToString(), "text/plain");

            using var reader = new StreamReader(ctx.Request.Body);
            var payload = JsonDocument.Parse(await reader.ReadToEndAsync(ct));
            var expectedClientState = config["GraphClientState"] ?? "certflow-webhook-secret";

            foreach (var n in payload.RootElement.GetProperty("value").EnumerateArray())
            {
                var clientState = n.TryGetProperty("clientState", out var cs) ? cs.GetString() : null;
                if (!string.Equals(clientState, expectedClientState, StringComparison.Ordinal))
                {
                    log.LogWarning("Dropping lifecycle notification with mismatched clientState.");
                    continue;
                }

                var lifecycleEvent = n.TryGetProperty("lifecycleEvent", out var le) ? le.GetString() : null;
                var subscriptionId = n.TryGetProperty("subscriptionId", out var si) ? si.GetString() : null;

                switch (lifecycleEvent)
                {
                    // A single PATCH carrying a new expiry both reauthorizes and renews, which
                    // Graph prefers over pairing /reauthorize with a separate update.
                    case "reauthorizationRequired" when subscriptionId is not null:
                        await graph.Subscriptions[subscriptionId].PatchAsync(new Subscription
                        {
                            ExpirationDateTime = DateTimeOffset.UtcNow.AddDays(3)
                        }, cancellationToken: ct);
                        log.LogInformation("Reauthorized subscription {Id}.", subscriptionId);
                        break;

                    // Recreation is handled by the Worker's renewal loop, which already knows
                    // how to build the subscription; logging here keeps that single-sourced.
                    case "subscriptionRemoved":
                        log.LogWarning("Subscription {Id} was removed by Graph — the renewal "
                                     + "service will recreate it on its next pass.", subscriptionId);
                        break;

                    // Graph dropped notifications (usually throttling). Say so loudly: some
                    // candidate mail was never delivered to us and needs a manual reconcile.
                    case "missed":
                        log.LogError("Graph reported MISSED notifications for subscription {Id}. "
                                   + "Inbound mail in that window was not processed.", subscriptionId);
                        break;

                    default:
                        log.LogInformation("Unhandled lifecycle event {Event} for {Id}.",
                            lifecycleEvent, subscriptionId);
                        break;
                }
            }

            return Results.Accepted();
        });
    }
}
