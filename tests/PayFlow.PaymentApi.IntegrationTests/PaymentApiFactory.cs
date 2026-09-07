using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayFlow.PaymentApi.Persistence;

namespace PayFlow.PaymentApi.IntegrationTests;

/// <summary>
/// Hosts the real API in-process against the local Docker Compose Postgres.
/// Nothing is substituted: the idempotency guarantee under test lives in a
/// database unique index, so a fake or in-memory provider would prove nothing.
/// </summary>
public sealed class PaymentApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Tests default to the Production environment, which would skip
        // appsettings.Development.json and leave PayFlowDb null. Running as
        // Development keeps the API's own configuration the single source of
        // truth for the connection string instead of duplicating it here.
        builder.UseEnvironment("Development");
    }

    public async ValueTask InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        try
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
        catch (NpgsqlException exception)
        {
            // Otherwise this surfaces as a bare socket error, which reads like a
            // broken test rather than missing infrastructure.
            throw new InvalidOperationException(
                "Could not reach the PayFlowDb Postgres instance. Start it with "
                + "'docker compose up -d' at the repository root before running the tests.",
                exception);
        }
    }

    /// <summary>
    /// Removes everything a test wrote under its idempotency key. Shared because
    /// a payment no longer arrives alone: it brings an outbox row, and a test that
    /// cleans up only the payment leaves that row orphaned in the dev database.
    /// </summary>
    /// <remarks>
    /// Outbox rows go first, while the payments that identify them are still
    /// present: an outbox row carries no idempotency key of its own and is
    /// reachable only through the payment id inside its jsonb payload.
    /// </remarks>
    public async Task DeleteTestDataAsync(Guid idempotencyKey)
    {
        await using var scope = Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await database.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "OutboxMessages"
             WHERE "Payload" ->> 'paymentId' IN (
                 SELECT "Id"::text FROM "Payments" WHERE "IdempotencyKey" = {idempotencyKey}
             )
             """);

        await database.Payments
            .Where(payment => payment.IdempotencyKey == idempotencyKey)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Runs work against the same database the API writes to, on a connection of
    /// its own, so assertions observe committed state rather than anything the
    /// request pipeline still has in memory.
    /// </summary>
    public async Task<T> QueryDatabaseAsync<T>(
        Func<PaymentsDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await query(database);
    }
}
