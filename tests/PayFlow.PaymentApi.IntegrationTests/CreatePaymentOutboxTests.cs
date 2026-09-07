using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayFlow.Contracts.Payments;
using PayFlow.PaymentApi.Outbox;
using PayFlow.PaymentApi.Payments;
using PayFlow.PaymentApi.Persistence;

namespace PayFlow.PaymentApi.IntegrationTests;

public sealed class CreatePaymentOutboxTests(PaymentApiFactory factory)
    : IClassFixture<PaymentApiFactory>, IAsyncLifetime
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    // Same isolation strategy as the idempotency tests: a fresh key per test
    // instance, so tests cannot collide with each other or with dev rows.
    private readonly Guid _idempotencyKey = Guid.NewGuid();

    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Accepted_payment_writes_one_pending_outbox_message()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var client = factory.CreateClient();

        using var response = await PostPaymentAsync(
            client,
            new CreatePaymentEndpoint.CreatePaymentRequest(42.00m, "GBP"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<CreatePaymentEndpoint.CreatePaymentResponse>(cancellationToken);
        var created = Assert.IsType<CreatePaymentEndpoint.CreatePaymentResponse>(body);

        var messages = await factory.QueryDatabaseAsync(database =>
            OutboxMessagesForPaymentAsync(database, created.Id, cancellationToken));

        var message = Assert.Single(messages);
        Assert.Equal(nameof(PaymentCreated), message.Type);
        Assert.Null(message.ProcessedAt);
        Assert.Equal(0, message.Attempts);

        var payload = JsonSerializer.Deserialize<PaymentCreated>(
            message.Payload,
            PayloadSerializerOptions);
        var published = Assert.IsType<PaymentCreated>(payload);

        Assert.Equal(created.Id, published.PaymentId);
        Assert.Equal(42.00m, published.Amount);
        Assert.Equal("GBP", published.Currency);

        // The handler reads the clock once and uses that instant for both rows.
        // If they ever diverge, the event is describing a different moment than
        // the state change it was written with.
        var payment = await factory.QueryDatabaseAsync(database => database.Payments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == created.Id, cancellationToken));

        // Both columns are timestamptz, so they truncate identically and can be
        // compared as they are.
        Assert.Equal(payment.CreatedAt, message.OccurredAt);

        // The payload cannot: jsonb stores the timestamp as the string .NET wrote,
        // keeping all seven tick digits, while timestamptz holds microseconds and
        // drops the seventh. Comparing raw values fails on that last digit alone,
        // which says nothing about the handler. Truncating to what Postgres can
        // store puts both sides at the same resolution.
        Assert.Equal(payment.CreatedAt, TruncateToMicroseconds(published.OccurredAt));
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Offset);

    [Fact]
    public async Task Idempotent_retry_does_not_write_a_second_outbox_message()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new CreatePaymentEndpoint.CreatePaymentRequest(19.99m, "USD");

        using var client = factory.CreateClient();

        // Sequential rather than concurrent: this is the plain client-retry path,
        // where the first request has certainly committed before the second one
        // starts and the duplicate is rejected by the unique index.
        using var first = await PostPaymentAsync(client, request, cancellationToken);
        using var second = await PostPaymentAsync(client, request, cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        var firstBody = await first.Content
            .ReadFromJsonAsync<CreatePaymentEndpoint.CreatePaymentResponse>(cancellationToken);
        var secondBody = await second.Content
            .ReadFromJsonAsync<CreatePaymentEndpoint.CreatePaymentResponse>(cancellationToken);

        var created = Assert.IsType<CreatePaymentEndpoint.CreatePaymentResponse>(firstBody);
        var replayed = Assert.IsType<CreatePaymentEndpoint.CreatePaymentResponse>(secondBody);

        Assert.Equal(created.Id, replayed.Id);

        var payments = await factory.QueryDatabaseAsync(database => database.Payments
            .AsNoTracking()
            .Where(payment => payment.IdempotencyKey == _idempotencyKey)
            .ToListAsync(cancellationToken));

        Assert.Single(payments);

        // The point of the test: one payment must mean one event. A retry that
        // wrote a second row would have consumers charging twice downstream.
        var messages = await factory.QueryDatabaseAsync(database =>
            OutboxMessagesForPaymentAsync(database, created.Id, cancellationToken));

        Assert.Single(messages);
    }

    /// <summary>
    /// Outbox rows carry no idempotency key, so they are located through the
    /// payment id inside the payload. Postgres reads that out of the jsonb column
    /// directly, which keeps the filter in the database rather than pulling every
    /// row back to sift through in the test.
    /// </summary>
    private static Task<List<OutboxMessage>> OutboxMessagesForPaymentAsync(
        PaymentsDbContext database,
        Guid paymentId,
        CancellationToken cancellationToken) =>
        database.OutboxMessages
            .FromSql($"""
                SELECT * FROM "OutboxMessages"
                WHERE "Payload" ->> 'paymentId' = {paymentId.ToString()}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

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

    // Keyed off the idempotency key rather than anything the test captured, so
    // cleanup still works when a test fails partway through.
    public async ValueTask DisposeAsync() => await factory.DeleteTestDataAsync(_idempotencyKey);
}
