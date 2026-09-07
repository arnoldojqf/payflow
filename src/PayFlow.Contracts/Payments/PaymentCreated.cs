namespace PayFlow.Contracts.Payments;

/// <summary>
/// Raised once a payment has been accepted and persisted, before any acquirer
/// call has been attempted. Carries the payment details the processor needs so
/// consumers do not have to read back from the API's database.
/// </summary>
/// <remarks>
/// This type crosses service boundaries as serialised JSON, so treat it as a
/// published contract: adding an optional member is safe, renaming or removing
/// one breaks consumers that are still running the previous version.
/// </remarks>
public sealed record PaymentCreated(
    Guid PaymentId,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt);
