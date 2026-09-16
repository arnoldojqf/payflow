using System.Collections.Concurrent;
using PayFlow.PaymentApi.Outbox;

namespace PayFlow.PaymentApi.IntegrationTests;

/// <summary>
/// An <see cref="IOutboxPublisher"/> that records what it was asked to publish,
/// and fails on demand.
/// </summary>
/// <remarks>
/// Stands in for the broker in the loop tests, where the question is which rows
/// the dispatcher claims, settles and retries — not whether the Service Bus SDK
/// works. <see cref="ServiceBusOutboxPublisherTests"/> covers the real transport.
/// </remarks>
public sealed class RecordingOutboxPublisher : IOutboxPublisher
{
    private readonly ConcurrentQueue<PendingOutboxMessage> _published = new();

    /// <summary>Set to throw on every publish, simulating a broker outage.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Awaited before publishing, to widen the window two dispatchers can collide in.</summary>
    public Func<Task>? BeforePublish { get; set; }

    public IReadOnlyCollection<PendingOutboxMessage> Published => _published;

    public async Task PublishAsync(PendingOutboxMessage message, CancellationToken cancellationToken)
    {
        if (BeforePublish is not null)
        {
            await BeforePublish();
        }

        if (FailWith is not null)
        {
            throw FailWith;
        }

        _published.Enqueue(message);
    }
}
