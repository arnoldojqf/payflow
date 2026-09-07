using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayFlow.Contracts.Payments;
using PayFlow.PaymentApi.Outbox;
using PayFlow.PaymentApi.Persistence;
using PayFlow.PaymentApi.Persistence.Configurations;

namespace PayFlow.PaymentApi.Payments;

public static partial class CreatePaymentEndpoint
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    // Web defaults give camelCase members, matching what a consumer on another
    // stack expects off the wire rather than the C# property names.
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/payments", HandleAsync)
            .WithName("CreatePayment");

        return endpoints;
    }

    private static async Task<Results<Accepted<CreatePaymentResponse>, ValidationProblem>> HandleAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKeyHeader,
        PaymentsDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var idempotencyKey = Guid.Empty;
        if (string.IsNullOrWhiteSpace(idempotencyKeyHeader))
        {
            errors[IdempotencyKeyHeader] = [$"The {IdempotencyKeyHeader} header is required."];
        }
        else if (!Guid.TryParse(idempotencyKeyHeader, out idempotencyKey))
        {
            errors[IdempotencyKeyHeader] = [$"The {IdempotencyKeyHeader} header must be a GUID."];
        }

        if (request.Amount <= 0)
        {
            errors[nameof(request.Amount)] = ["Amount must be greater than zero."];
        }

        if (request.Currency is null || !CurrencyPattern().IsMatch(request.Currency))
        {
            errors[nameof(request.Currency)] =
                ["Currency must be a three-letter uppercase ISO 4217 code."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // One reading of the clock for both rows: the payment's CreatedAt and the
        // event's OccurredAt describe the same instant, so they must not differ.
        var now = timeProvider.GetUtcNow();

        var payment = Payment.Create(
            idempotencyKey,
            request.Amount,
            request.Currency!,
            now);

        var outboxMessage = OutboxMessage.Create(
            nameof(PaymentCreated),
            JsonSerializer.Serialize(
                new PaymentCreated(payment.Id, payment.Amount, payment.Currency, now),
                PayloadSerializerOptions),
            now);

        database.Payments.Add(payment);
        database.OutboxMessages.Add(outboxMessage);

        try
        {
            // Both inserts go out under this one call. EF wraps a multi-statement
            // batch in its own transaction, so the payment and its event commit
            // together or not at all — which is the whole point of the outbox.
            // An explicit BeginTransaction would add nothing: there is no
            // read-then-write needing a stricter isolation level, and no second
            // SaveChanges for a transaction to span.
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyViolation(exception))
        {
            // The key was already used, so this is a client retry: replay the
            // response from the original request instead of creating a duplicate.
            //
            // The rejected inserts are still tracked as Added; detaching them stops
            // a later SaveChanges on this request-scoped context from retrying them.
            // Both are defensive today, since this path returns without saving
            // again — the outbox entry is detached alongside the payment so that
            // stays true if a save is ever added below it.
            database.Entry(payment).State = EntityState.Detached;
            database.Entry(outboxMessage).State = EntityState.Detached;

            var existing = await database.Payments
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.IdempotencyKey == idempotencyKey,
                    cancellationToken);

            // The row that won the race should be visible now. If it is not, the
            // cause is something other than a plain retry, so surface it.
            if (existing is null)
            {
                throw;
            }

            return Accepted(existing);
        }

        return Accepted(payment);
    }

    private static Accepted<CreatePaymentResponse> Accepted(Payment payment) =>
        // No Location URI yet: there is no endpoint to read a payment back from.
        TypedResults.Accepted(
            (string?)null,
            new CreatePaymentResponse(payment.Id, payment.Status.ToString()));

    private static bool IsIdempotencyKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PaymentConfiguration.IdempotencyKeyIndexName,
        };

    // \A and \z rather than ^ and $, which would also accept a trailing newline.
    [GeneratedRegex(@"\A[A-Z]{3}\z")]
    private static partial Regex CurrencyPattern();

    public sealed record CreatePaymentRequest(decimal Amount, string? Currency);

    public sealed record CreatePaymentResponse(Guid Id, string Status);
}
