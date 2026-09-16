namespace PayFlow.PaymentApi.Outbox;

/// <summary>
/// One outbox row as the dispatcher reads it: the fields needed to publish,
/// nothing else.
/// </summary>
/// <remarks>
/// Deliberately not the <see cref="OutboxMessage"/> entity. That type belongs to
/// the write path, where EF materialises it through a private constructor and
/// tracks it for changes; the dispatcher reads with Dapper, updates with one
/// set-based statement, and has no use for tracking. Keeping the read shape
/// separate is the same EF-writes/Dapper-reads split the project applies
/// elsewhere.
/// </remarks>
public sealed record PendingOutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAt,
    int Attempts);
