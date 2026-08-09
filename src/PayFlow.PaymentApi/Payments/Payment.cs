namespace PayFlow.PaymentApi.Payments;

public sealed class Payment
{
    // EF Core materialises instances through this constructor; application code
    // goes through Create so a Payment can never exist in an invalid state.
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = null!;

    public PaymentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Payment Create(
        Guid idempotencyKey,
        decimal amount,
        string currency,
        DateTimeOffset createdAt) =>
        new()
        {
            // Version 7 GUIDs are time-ordered, so primary-key inserts stay
            // append-mostly instead of scattering across the index the way
            // random version 4 values do.
            Id = Guid.CreateVersion7(createdAt),
            IdempotencyKey = idempotencyKey,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Pending,
            CreatedAt = createdAt,
        };
}
