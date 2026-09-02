using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CertFlow.Worker.Services;

public class GraphSubscriptionRenewalService(
    GraphServiceClient graph,
    IConfiguration config,
    ILogger<GraphSubscriptionRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var mailboxId = config["MailboxEmail"]!;
                var webhookUrl = $"{config["WebhookBaseUrl"]}/api/emails/graph-webhook";

                var subs = await graph.Subscriptions.GetAsync(cancellationToken: ct);
                var certflowSub = subs?.Value?.FirstOrDefault(s => s.NotificationUrl == webhookUrl);

                if (certflowSub?.ExpirationDateTime < DateTimeOffset.UtcNow.AddHours(24))
                {
                    await graph.Subscriptions[certflowSub.Id].PatchAsync(new Subscription
                    {
                        ExpirationDateTime = DateTimeOffset.UtcNow.AddDays(3)
                    }, cancellationToken: ct);
                    logger.LogInformation("Graph subscription {Id} renewed.", certflowSub.Id);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to renew Graph subscription.");
            }

            await Task.Delay(TimeSpan.FromHours(12), ct);
        }
    }
}
