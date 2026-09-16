using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using PayFlow.PaymentApi.Persistence;

namespace PayFlow.PaymentApi.Outbox;

/// <summary>
/// Publishes pending outbox rows, oldest first, and marks them processed in the
/// same transaction that claimed them.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="BackgroundService"/> rather than a bare
/// <see cref="IHostedService"/>: the work is one long-running loop with nothing
/// to do at startup, which is what the base class exists for. It starts
/// <see cref="ExecuteAsync"/> off the startup path, hands it a token cancelled
/// on shutdown, and waits for it to finish when the host stops.
/// </para>
/// <para>
/// Rationale for the claim-and-publish design, including what at-least-once
/// delivery costs consumers, is in docs/adr/0001-outbox-dispatch.md.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    IOutboxPublisher publisher,
    IOptions<OutboxDispatcherOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// Claims a batch of pending rows for this dispatcher alone.
    /// </summary>
    /// <remarks>
    /// FOR UPDATE SKIP LOCKED is what makes more than one replica safe: the row
    /// locks are held until this transaction ends, and a second dispatcher steps
    /// over the locked rows instead of blocking on them or publishing them a
    /// second time. The WHERE and ORDER BY match the (ProcessedAt, OccurredAt)
    /// index, so the scan stops at the LIMIT rather than reading dispatched
    /// history.
    /// </remarks>
    private const string ClaimPendingSql =
        """
        SELECT "Id", "Type", "Payload", "OccurredAt", "Attempts"
        FROM "OutboxMessages"
        WHERE "ProcessedAt" IS NULL
        ORDER BY "OccurredAt"
        LIMIT @BatchSize
        FOR UPDATE SKIP LOCKED
        """;

    private const string MarkProcessedSql =
        """
        UPDATE "OutboxMessages"
        SET "ProcessedAt" = @ProcessedAt, "Attempts" = "Attempts" + 1
        WHERE "Id" = ANY(@Ids)
        """;

    // Failures only move the counter. The row stays pending, so the next tick
    // picks it up again; capping the retries is a later task.
    private const string RecordAttemptSql =
        """
        UPDATE "OutboxMessages"
        SET "Attempts" = "Attempts" + 1
        WHERE "Id" = ANY(@Ids)
        """;

    // Here rather than only in Program.cs, so a dispatcher constructed directly —
    // as the tests do — reads timestamps through the same mappings the host uses.
    static OutboxDispatcher() => DapperMappings.EnsureRegistered();

    private readonly OutboxDispatcherOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer over a Task.Delay loop or a System.Threading.Timer: it
        // cannot overlap ticks with itself, a tick missed while a long batch ran
        // collapses into one rather than queueing up, and taking a TimeProvider
        // lets tests drive the schedule instead of waiting on a real clock.
        using var timer = new PeriodicTimer(_options.PollingInterval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await DrainAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a fault: let ExecuteAsync complete quietly.
        }
    }

    /// <summary>
    /// Keeps dispatching while batches come back full, so a backlog drains at
    /// the speed of the broker instead of one batch per tick.
    /// </summary>
    /// <remarks>
    /// Continuing requires the batch to have been published in full, not merely
    /// to have been full. Failed rows stay pending, so a broker outage would
    /// otherwise have this loop re-claim the same rows forever, hammering a
    /// broker that is already unwell and never yielding to the timer. Backing
    /// off to the next tick is the correct response to a batch that did not
    /// entirely succeed.
    /// </remarks>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            BatchResult batch;
            do
            {
                batch = await DispatchBatchAsync(cancellationToken);
            }
            while (batch.Claimed == _options.BatchSize
                && batch.Published == batch.Claimed
                && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An exception escaping ExecuteAsync stops the host by default, and a
            // broker outage or a dropped database connection must not take the
            // payment API down with it. The rows are still pending, so the next
            // tick retries them.
            logger.LogError(
                exception,
                "Outbox dispatch failed. Pending messages will be retried on the next tick.");
        }
    }

    /// <summary>What one batch claimed and how much of it reached the broker.</summary>
    internal readonly record struct BatchResult(int Claimed, int Published);

    /// <summary>
    /// Claims, publishes and settles one batch.
    /// </summary>
    internal async Task<BatchResult> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var pending = (await connection.QueryAsync<PendingOutboxMessage>(new CommandDefinition(
                ClaimPendingSql,
                new { _options.BatchSize },
                transaction,
                cancellationToken: cancellationToken)))
            .AsList();

        if (pending.Count == 0)
        {
            return new BatchResult(Claimed: 0, Published: 0);
        }

        var published = new List<Guid>(pending.Count);
        var failed = new List<Guid>();

        foreach (var message in pending)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                published.Add(message.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Publishing outbox message {MessageId} of type {MessageType} failed on attempt {Attempt}.",
                    message.Id,
                    message.Type,
                    message.Attempts + 1);

                failed.Add(message.Id);
            }
        }

        if (published.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                MarkProcessedSql,
                new { ProcessedAt = timeProvider.GetUtcNow(), Ids = published.ToArray() },
                transaction,
                cancellationToken: cancellationToken));
        }

        if (failed.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                RecordAttemptSql,
                new { Ids = failed.ToArray() },
                transaction,
                cancellationToken: cancellationToken));
        }

        // Until this commit lands, every row in the batch is still pending. A
        // crash here republishes the batch rather than losing it: delivery is
        // at-least-once by design, and the message id keeps the duplicate
        // recognisable downstream.
        await transaction.CommitAsync(cancellationToken);

        if (published.Count > 0)
        {
            logger.LogInformation(
                "Dispatched {PublishedCount} outbox message(s); oldest had waited {LagMilliseconds}ms.",
                published.Count,
                (timeProvider.GetUtcNow() - pending[0].OccurredAt).TotalMilliseconds);
        }

        return new BatchResult(pending.Count, published.Count);
    }
}
