using Azure.Messaging.ServiceBus;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CertFlow.Infrastructure.Messaging;

/// <summary>
/// Senders are cached per queue. Creating one per publish establishes a fresh AMQP link
/// every time and never releases it — cost that lands directly in the Graph webhook's
/// three-second response budget.
/// </summary>
public class ServiceBusPublisher(ServiceBusClient client) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServiceBusSender> senders = new();

    public async Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
    {
        var sender = senders.GetOrAdd(queueName, client.CreateSender);
        var json = JsonSerializer.Serialize(message);
        await sender.SendMessageAsync(new ServiceBusMessage(json), ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in senders.Values)
            await sender.DisposeAsync();
        senders.Clear();
        GC.SuppressFinalize(this);
    }
}
