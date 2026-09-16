using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace PayFlow.PaymentApi.Outbox;

/// <summary>
/// Publishes outbox messages to the Service Bus topic every payment event goes
/// to. Subscribers filter on <see cref="ServiceBusMessage.Subject"/>, so adding
/// an event type needs no new topic.
/// </summary>
public sealed class ServiceBusOutboxPublisher : IOutboxPublisher, IAsyncDisposable
{
    public const string PayloadContentType = "application/json";

    private readonly ServiceBusSender _sender;

    public ServiceBusOutboxPublisher(ServiceBusClient client, IOptions<ServiceBusOptions> options) =>
        // One sender for the lifetime of the process: senders are thread-safe and
        // hold the AMQP link, so creating one per publish would pay for a link
        // handshake on every message.
        _sender = client.CreateSender(options.Value.TopicName);

    public Task PublishAsync(PendingOutboxMessage message, CancellationToken cancellationToken)
    {
        var serviceBusMessage = new ServiceBusMessage(message.Payload)
        {
            // The outbox row id, not a new value per attempt. At-least-once
            // dispatch means the same row can be published twice — a crash
            // between the broker's acknowledgement and the commit is enough —
            // and a stable MessageId is what lets duplicate detection on the
            // topic, and consumers, recognise the second copy as the same event.
            MessageId = message.Id.ToString(),
            Subject = message.Type,
            ContentType = PayloadContentType,
        };

        return _sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}
