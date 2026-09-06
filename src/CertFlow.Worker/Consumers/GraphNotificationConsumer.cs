using Azure.Messaging.ServiceBus;
using CertFlow.Contracts.Models;
using CertFlow.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CertFlow.Worker.Consumers;

/// <summary>
/// Does the work the Graph webhook cannot afford to do inline: fetch the message, decide
/// whether the sender is allowed, and route it to the inbound or reply queue.
/// </summary>
public class GraphNotificationConsumer(
    ServiceBusClient sbClient,
    GraphServiceClient graph,
    ServiceBusPublisher publisher,
    IConfiguration config,
    ILogger<GraphNotificationConsumer> logger) : BackgroundService
{
    private static readonly Regex ReplyTokenRegex =
        new(@"\[REF:[A-Za-z0-9\-_]+\]", RegexOptions.Compiled);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var processor = sbClient.CreateProcessor("notifications");
        processor.ProcessMessageAsync += HandleAsync;
        processor.ProcessErrorAsync += e =>
        {
            logger.LogError(e.Exception, "Service Bus error on notifications");
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync(ct);
        await Task.Delay(Timeout.Infinite, ct);
        await processor.StopProcessingAsync();
    }

    private async Task HandleAsync(ProcessMessageEventArgs args)
    {
        var notification = JsonSerializer.Deserialize<GraphNotification>(args.Message.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var mailboxId = config["MailboxEmail"]!;

        var message = await graph.Users[mailboxId].Messages[notification.MessageId]
            .GetAsync(req =>
            {
                req.QueryParameters.Select = ["id", "subject", "body", "from", "receivedDateTime"];
            }, args.CancellationToken);

        if (message is null)
        {
            logger.LogWarning("Message {Id} no longer exists — skipping.", notification.MessageId);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        var senderEmail = message.From?.EmailAddress?.Address ?? string.Empty;
        var subject = message.Subject ?? string.Empty;

        if (!await IsAcceptedSenderAsync(senderEmail, mailboxId, args.CancellationToken))
        {
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        // A [REF:token] means the candidate is answering a proposal. Proposals reply in-thread
        // without rewriting the subject, so the token is carried in the body and comes back
        // inside the quoted text — the subject alone is no longer enough to route on.
        var body = message.Body?.Content ?? string.Empty;
        var queueName = ReplyTokenRegex.IsMatch(subject) || ReplyTokenRegex.IsMatch(body)
            ? "email-replies"
            : "inbound-emails";

        await publisher.PublishAsync(queueName, new EmailMessage(
            MessageId: notification.MessageId,
            SenderEmail: senderEmail,
            Subject: subject,
            Body: body,
            ReceivedAt: message.ReceivedDateTime ?? DateTimeOffset.UtcNow,
            InReplyToMessageId: null), args.CancellationToken);

        logger.LogInformation("Routed message from {Sender} to {Queue}.", senderEmail, queueName);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    /// <summary>
    /// Gate before any agent runs: unknown senders are dropped silently rather than bounced,
    /// so a stranger emailing the mailbox costs nothing in model tokens.
    /// </summary>
    private async Task<bool> IsAcceptedSenderAsync(string senderEmail, string mailboxId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(senderEmail))
        {
            logger.LogWarning("Dropping notification with no sender address.");
            return false;
        }

        // Mail this system sent. The usual "shared mailboxes are disabled accounts" check does
        // not apply here — the mailbox is provisioned as a licensed, enabled user, so without
        // this an outbound proposal could feed itself back in as a new request.
        if (string.Equals(senderEmail, mailboxId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Dropping mail sent by the mailbox itself ({Sender}).", senderEmail);
            return false;
        }

        try
        {
            var user = await graph.Users[senderEmail]
                .GetAsync(r => r.QueryParameters.Select = ["id", "accountEnabled"], ct);

            if (user is null || user.AccountEnabled != true)
            {
                logger.LogInformation("Dropping mail from disabled or unknown sender {Sender}.", senderEmail);
                return false;
            }
            return true;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // Not a tenant user — external sender, drop without bouncing.
            logger.LogInformation("Dropping mail from non-tenant sender {Sender}.", senderEmail);
            return false;
        }
    }
}
