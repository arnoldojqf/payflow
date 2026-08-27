using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PayFlow.PaymentApi.Payments;

namespace PayFlow.PaymentApi.IntegrationTests;

public sealed class CreatePaymentIdempotencyTests(PaymentApiFactory factory)
    : IClassFixture<PaymentApiFactory>, IAsyncLifetime
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    // A fresh key per test instance is the isolation strategy. Wrapping the test
    // in a rolled-back transaction is not an option: each HTTP request gets its
    // own pooled connection, and the unique violation only fires once the winning
    // insert commits, so an uncommitted shared transaction would remove the very
    // race under test. Scoping to a random key instead means tests cannot collide
    // with each other or with existing dev rows, and an aborted run leaves nothing
    // behind that could affect the next one.
    private readonly Guid _idempotencyKey = Guid.NewGuid();

    [Fact]
    public async Task Concurrent_requests_sharing_an_idempotency_key_create_a_single_payment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new CreatePaymentEndpoint.CreatePaymentRequest(125.50m, "EUR");

        using var client = factory.CreateClient();

        // Both are started before either is awaited, so they are genuinely in
        // flight together. Note this makes the interleaving likely, not certain:
        // one request may commit before the other reaches its insert. Either
        // ordering lands on the same unique-violation path, so the assertions
        // hold regardless — what is proven is that the outcome is idempotent,
        // not that a particular interleaving occurred.
        var firstCall = PostPaymentAsync(client, request, cancellationToken);
        var secondCall = PostPaymentAsync(client, request, cancellationToken);

        var responses = await Task.WhenAll(firstCall, secondCall);

        Assert.All(responses, response =>
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));

        var bodies = await Task.WhenAll(responses.Select(response =>
            response.Content
                .ReadFromJsonAsync<CreatePaymentEndpoint.CreatePaymentResponse>(cancellationToken)));

        var firstBody = Assert.IsType<CreatePaymentEndpoint.CreatePaymentResponse>(bodies[0]);
        var secondBody = Assert.IsType<CreatePaymentEndpoint.CreatePaymentResponse>(bodies[1]);

        Assert.NotEqual(Guid.Empty, firstBody.Id);
        Assert.Equal(firstBody.Id, secondBody.Id);

        // Straight to the database: two responses agreeing on an id is not by
        // itself proof that only one row was written.
        var stored = await factory.QueryDatabaseAsync(database => database.Payments
            .AsNoTracking()
            .Where(payment => payment.IdempotencyKey == _idempotencyKey)
            .ToListAsync(cancellationToken));

        var persisted = Assert.Single(stored);
        Assert.Equal(firstBody.Id, persisted.Id);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private Task<HttpResponseMessage> PostPaymentAsync(
        HttpClient client,
        CreatePaymentEndpoint.CreatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(request),
        };

        message.Headers.Add(IdempotencyKeyHeader, _idempotencyKey.ToString());

        return client.SendAsync(message, cancellationToken);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() =>
        await factory.QueryDatabaseAsync(database => database.Payments
            .Where(payment => payment.IdempotencyKey == _idempotencyKey)
            .ExecuteDeleteAsync());
}
