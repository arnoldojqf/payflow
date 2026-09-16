namespace PayFlow.PaymentApi.IntegrationTests;

/// <summary>
/// Puts every test that touches Postgres in one collection, which xUnit runs
/// without parallelism.
/// </summary>
/// <remarks>
/// Per-test idempotency keys keep the payment rows apart, but they cannot keep
/// the dispatcher apart from anything: it claims whichever rows are pending,
/// whoever wrote them. Running the dispatcher tests alongside the endpoint tests
/// would let it publish their outbox rows and stamp ProcessedAt on rows those
/// tests assert are still pending. Serialising the collection removes the race;
/// sharing the factory also means one host and one connection pool for the run.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PaymentApiFactory>
{
    public const string Name = "Database";
}
