using Azure.Messaging.ServiceBus;
using System.Text.Json;

namespace CertFlow.Infrastructure.Messaging;

public class ServiceBusPublisher(ServiceBusClient client)
{
    public async Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
    {
        var sender = client.CreateSender(queueName);
        var json = JsonSerializer.Serialize(message);
        await sender.SendMessageAsync(new ServiceBusMessage(json), ct);
    }
}
