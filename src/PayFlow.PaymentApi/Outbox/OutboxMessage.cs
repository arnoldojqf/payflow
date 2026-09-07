namespace PayFlow.PaymentApi.Outbox;

/// <summary>
/// An event awaiting publication, written in the same transaction as the state
/// change that produced it. Persisting both atomically is what stops the pair
/// from drifting apart: a crash after the commit leaves the message on disk for
/// a later dispatch, and a crash before it discards the state change too.
/// </summary>
public sealed class OutboxMessage
{
    // EF Core materialises instances through this constructor; application code
    // goes through Create so an OutboxMessage can never exist in an invalid state.
    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Name of the event contract, for example "PaymentCreated".</summary>
    public string Type { get; private set; } = null!;

    /// <summary>The serialised event, stored as jsonb.</summary>
    public string Payload { get; private set; } = null!;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Null until dispatched; the dispatcher's pending-work marker.</summary>
    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>Dispatch attempts so far, for the retry policy added with the dispatcher.</summary>
    public int Attempts { get; private set; }

    public static OutboxMessage Create(string type, string payload, DateTimeOffset occurredAt) =>
        new()
        {
            // Version 7 GUIDs are time-ordered, so primary-key inserts stay
            // append-mostly instead of scattering across the index the way
            // random version 4 values do.
            Id = Guid.CreateVersion7(occurredAt),
            Type = type,
            Payload = payload,
            OccurredAt = occurredAt,
            ProcessedAt = null,
            Attempts = 0,
        };
}
