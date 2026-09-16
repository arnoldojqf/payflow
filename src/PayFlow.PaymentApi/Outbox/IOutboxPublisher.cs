namespace PayFlow.PaymentApi.Outbox;

/// <summary>
/// Sends an outbox message to the broker. The dispatcher owns when and in what
/// order messages are published; an implementation owns only the transport.
/// </summary>
public interface IOutboxPublisher
{
    /// <summary>
    /// Publishes a single message. Returning means the broker acknowledged it;
    /// throwing means it did not, and the dispatcher will try the row again.
    /// </summary>
    Task PublishAsync(PendingOutboxMessage message, CancellationToken cancellationToken);
}
