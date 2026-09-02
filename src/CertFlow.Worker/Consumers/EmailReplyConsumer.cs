using Azure.Messaging.ServiceBus;
using CertFlow.Application.Handlers;
using CertFlow.Application.Interfaces;
using CertFlow.Contracts.Commands;
using CertFlow.Contracts.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CertFlow.Worker.Consumers;

public class EmailReplyConsumer(
    ServiceBusClient sbClient,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailReplyConsumer> logger) : BackgroundService
{
    private static readonly Regex TokenRegex = new(@"\[REF:([A-Za-z0-9\-_]+)\]", RegexOptions.Compiled);
    private static readonly Regex ChoiceRegex = new(@"^\s*([123]|yes|YES)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

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
        var email = JsonSerializer.Deserialize<EmailMessage>(args.Message.Body)!;

        var tokenMatch = TokenRegex.Match(email.Subject);
        if (!tokenMatch.Success)
        {
            logger.LogWarning("Reply email missing correlation token in subject: {Subject}", email.Subject);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        var token = tokenMatch.Groups[1].Value;

        using var scope = scopeFactory.CreateScope();
        var requestRepo = scope.ServiceProvider.GetRequiredService<IRescheduleRequestRepository>();
        var handler = scope.ServiceProvider.GetRequiredService<ConfirmRescheduleHandler>();

        var request = await requestRepo.GetByCorrelationTokenAsync(token, args.CancellationToken);
        if (request is null)
        {
            logger.LogWarning("No reschedule request found for token {Token}", token);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        // Parse choice from email body (plain text extract)
        var plainBody = StripHtml(email.Body);
        var choiceMatch = ChoiceRegex.Match(plainBody);
        int slotIndex = 0;   // default to option 1 / YES

        if (choiceMatch.Success)
        {
            var choice = choiceMatch.Value.Trim().ToUpper();
            slotIndex = choice switch { "2" => 1, "3" => 2, _ => 0 };
        }

        if (slotIndex >= request.ProposedSlotIds.Count)
        {
            logger.LogWarning("Choice {Index} out of range for request {RequestId}", slotIndex, request.Id);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        var selectedSlot = request.ProposedSlotIds[slotIndex];
        await handler.HandleAsync(new ConfirmRescheduleCommand(request.Id, selectedSlot, "Email"), args.CancellationToken);

        logger.LogInformation("Reschedule request {RequestId} committed via email reply.", request.Id);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private static string StripHtml(string html) =>
        Regex.Replace(html, "<[^>]*>", " ");
}
