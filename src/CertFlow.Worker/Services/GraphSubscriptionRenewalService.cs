using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CertFlow.Worker.Services;

/// <summary>
/// Keeps the mailbox change-notification subscription alive. Graph subscriptions on Outlook
/// messages expire after at most 7 days, and Graph can also remove one outright, so this both
/// renews an expiring subscription and recreates a missing one.
/// </summary>
public class GraphSubscriptionRenewalService(
    GraphServiceClient graph,
    IConfiguration config,
    ILogger<GraphSubscriptionRenewalService> logger) : BackgroundService
{
    // Outlook message subscriptions cap at 10,080 minutes (under 7 days). Three days keeps
    // a wide margin under that ceiling while surviving a weekend of downtime.
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromDays(3);
    private static readonly TimeSpan RenewWhenWithin = TimeSpan.FromHours(24);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureSubscriptionAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure Graph subscription.");
            }

            await Task.Delay(CheckInterval, ct);
        }
    }

    private async Task EnsureSubscriptionAsync(CancellationToken ct)
    {
        var mailboxId = config["MailboxEmail"]!;
        var baseUrl = config["WebhookBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogWarning("WebhookBaseUrl not configured — skipping subscription check.");
            return;
        }

        var webhookUrl = $"{baseUrl}/api/emails/graph-webhook";
        var lifecycleUrl = $"{baseUrl}/api/emails/graph-lifecycle";

        var subs = await graph.Subscriptions.GetAsync(cancellationToken: ct);
        var existing = subs?.Value?.FirstOrDefault(s => s.NotificationUrl == webhookUrl);

        // A null subscription must be recreated. The previous `existing?.Expiration < now`
        // form silently did nothing here, because a null comparison is always false — so a
        // removed or lapsed subscription meant the system stopped receiving mail forever.
        if (existing is null)
        {
            await CreateAsync(mailboxId, webhookUrl, lifecycleUrl, ct);
            return;
        }

        // lifecycleNotificationUrl cannot be added by PATCH; Graph requires the subscription
        // to be recreated to attach one.
        if (string.IsNullOrEmpty(existing.LifecycleNotificationUrl))
        {
            logger.LogInformation("Subscription {Id} has no lifecycle URL — recreating.", existing.Id);
            await graph.Subscriptions[existing.Id].DeleteAsync(cancellationToken: ct);
            await CreateAsync(mailboxId, webhookUrl, lifecycleUrl, ct);
            return;
        }

        if (existing.ExpirationDateTime is null
            || existing.ExpirationDateTime <= DateTimeOffset.UtcNow.Add(RenewWhenWithin))
        {
            // PATCH with a new expiry both renews and reauthorizes in one operation, which
            // is what Graph recommends over a separate /reauthorize call.
            await graph.Subscriptions[existing.Id].PatchAsync(new Subscription
            {
                ExpirationDateTime = DateTimeOffset.UtcNow.Add(SubscriptionLifetime)
            }, cancellationToken: ct);
            logger.LogInformation("Graph subscription {Id} renewed.", existing.Id);
        }
    }

    private async Task CreateAsync(string mailboxId, string webhookUrl, string lifecycleUrl, CancellationToken ct)
    {
        var created = await graph.Subscriptions.PostAsync(new Subscription
        {
            ChangeType = "created",
            NotificationUrl = webhookUrl,
            LifecycleNotificationUrl = lifecycleUrl,
            Resource = $"users/{mailboxId}/mailFolders/Inbox/messages",
            ExpirationDateTime = DateTimeOffset.UtcNow.Add(SubscriptionLifetime),
            ClientState = config["GraphClientState"] ?? "certflow-webhook-secret"
        }, cancellationToken: ct);

        logger.LogInformation("Created Graph subscription {Id}, expires {Expiry}.",
            created?.Id, created?.ExpirationDateTime);
    }
}
