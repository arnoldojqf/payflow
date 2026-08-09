namespace PayFlow.PaymentApi.Payments;

/// <summary>
/// Lifecycle of a payment. Persisted as a string so the stored value stays
/// readable and stable if the enum members are ever reordered.
/// </summary>
public enum PaymentStatus
{
    Pending,
    Authorized,
    Failed,
}
