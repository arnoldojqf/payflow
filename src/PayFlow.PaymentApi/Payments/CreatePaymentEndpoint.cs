using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayFlow.PaymentApi.Persistence;
using PayFlow.PaymentApi.Persistence.Configurations;

namespace PayFlow.PaymentApi.Payments;

public static partial class CreatePaymentEndpoint
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

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

        var payment = Payment.Create(
            idempotencyKey,
            request.Amount,
            request.Currency!,
            timeProvider.GetUtcNow());

        database.Payments.Add(payment);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyViolation(exception))
        {
            // The key was already used, so this is a client retry: replay the
            // response from the original request instead of creating a duplicate.
            //
            // The rejected insert is still tracked as Added; detaching it stops a
            // later SaveChanges on this request-scoped context from retrying it.
            database.Entry(payment).State = EntityState.Detached;

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
