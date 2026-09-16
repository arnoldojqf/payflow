using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PayFlow.Contracts.Payments;
using PayFlow.PaymentApi.Outbox;

namespace PayFlow.PaymentApi.IntegrationTests;

/// <summary>
/// Drives the dispatcher against the real database with a publisher the test
/// controls, so claiming, settling and retrying are observed in Postgres rather
/// than in a mock.
/// </summary>
/// <remarks>
/// These tests assert only about rows they seeded. A dispatcher claims whatever
/// is pending, so a run may also publish rows left in the dev database by hand;
/// counting all rows in the table would make the outcome depend on that history.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class OutboxDispatcherTests(PaymentApiFactory factory) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly List<Guid> _seeded = [];

    [Fact]
    public async Task Pending_message_is_published_and_marked_processed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var publisher = new RecordingOutboxPublisher();
        var dispatcher = CreateDispatcher(publisher);
        var messageId = (await SeedPendingAsync(1))[0];

        await dispatcher.DispatchBatchAsync(cancellationToken);

        var published = Assert.Single(publisher.Published, message => message.Id == messageId);
        Assert.Equal(nameof(PaymentCreated), published.Type);

        var row = await ReadAsync(messageId, cancellationToken);

        Assert.NotNull(row.ProcessedAt);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task Publish_failure_leaves_the_message_pending_and_counts_the_attempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var publisher = new RecordingOutboxPublisher
        {
            FailWith = new InvalidOperationException("The broker is unreachable."),
        };

        var dispatcher = CreateDispatcher(publisher);
        var messageId = (await SeedPendingAsync(1))[0];

        // The dispatcher must swallow this: an unhandled exception from
        // ExecuteAsync stops the host, and a broker outage cannot be allowed to
        // take the payment API down.
        await dispatcher.DispatchBatchAsync(cancellationToken);

        Assert.Empty(publisher.Published);

        var row = await ReadAsync(messageId, cancellationToken);

        // Still pending, so the next tick retries it, with the counter showing
        // the failed attempt.
        Assert.Null(row.ProcessedAt);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task Concurrent_dispatchers_publish_each_message_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Small batches against ten rows force repeated claims, so the two
        // dispatchers keep meeting on the same table.
        const int MessageCount = 10;
        const int BatchSize = 2;

        // Publishing slowly holds each claim open, which is when a second
        // dispatcher would collide with it: without SKIP LOCKED it would either
        // block on those rows or, worse, claim and publish them a second time.
        var delay = () => Task.Delay(25, cancellationToken);

        var first = new RecordingOutboxPublisher { BeforePublish = delay };
        var second = new RecordingOutboxPublisher { BeforePublish = delay };

        var seeded = await SeedPendingAsync(MessageCount);

        await Task.WhenAll(
            DrainAsync(CreateDispatcher(first, BatchSize), cancellationToken),
            DrainAsync(CreateDispatcher(second, BatchSize), cancellationToken));

        var published = first.Published
            .Concat(second.Published)
            .Select(message => message.Id)
            .Where(seeded.Contains)
            .ToList();

        // Both halves matter: every row went out, and none went out twice.
        Assert.Equal(MessageCount, published.Count);
        Assert.Equal(MessageCount, published.Distinct().Count());

        var rows = await factory.QueryDatabaseAsync(database => database.OutboxMessages
            .AsNoTracking()
            .Where(message => seeded.Contains(message.Id))
            .ToListAsync(cancellationToken));

        Assert.All(rows, row => Assert.NotNull(row.ProcessedAt));
    }

    [Fact]
    public async Task Hosted_service_dispatches_between_start_and_stop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var publisher = new RecordingOutboxPublisher();
        var time = new FakeTimeProvider();
        var dispatcher = CreateDispatcher(publisher, timeProvider: time);

        var beforeStart = (await SeedPendingAsync(1))[0];

        // StartAsync returns as soon as the loop is running, so this exercises
        // the hosted-service lifecycle without a web host: no HTTP, no
        // WebApplicationFactory, just the service's own start and stop.
        await dispatcher.StartAsync(cancellationToken);

        try
        {
            await AdvanceUntilAsync(
                time,
                () => publisher.Published.Any(message => message.Id == beforeStart));
        }
        finally
        {
            // StopAsync cancels the stopping token and waits for ExecuteAsync to
            // return — but it waits forever on a loop that ignores the token
            // unless its own token can give up. Bounding it here means such a
            // dispatcher fails the assertion below instead of hanging the run.
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dispatcher.StopAsync(stopping.Token);
        }

        Assert.True(
            dispatcher.ExecuteTask is { IsCompleted: true },
            "ExecuteAsync had not returned after StopAsync: the loop is ignoring its stopping token.");

        var afterStop = (await SeedPendingAsync(1))[0];

        // A stopped dispatcher stays stopped: further ticks must do nothing.
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100, cancellationToken);

        Assert.DoesNotContain(publisher.Published, message => message.Id == afterStop);

        var row = await ReadAsync(afterStop, cancellationToken);
        Assert.Null(row.ProcessedAt);
    }

    private OutboxDispatcher CreateDispatcher(
        IOutboxPublisher publisher,
        int batchSize = 10,
        TimeProvider? timeProvider = null) =>
        new(
            factory.DataSource,
            publisher,
            Options.Create(new OutboxDispatcherOptions
            {
                BatchSize = batchSize,
                PollingInterval = TimeSpan.FromMilliseconds(100),
            }),
            timeProvider ?? TimeProvider.System,
            NullLogger<OutboxDispatcher>.Instance);

    /// <summary>
    /// Dispatches until nothing is left, with a ceiling on the number of rounds.
    /// </summary>
    /// <remarks>
    /// The ceiling is what turns a dispatcher that claims rows without settling
    /// them into a failure instead of a hang: unbounded, this loop would keep
    /// re-claiming the same rows until the test run was killed, which tells a
    /// reader nothing about what broke.
    /// </remarks>
    private static async Task DrainAsync(
        OutboxDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        const int MaxRounds = 50;

        for (var round = 0; round < MaxRounds; round++)
        {
            if ((await dispatcher.DispatchBatchAsync(cancellationToken)).Claimed == 0)
            {
                return;
            }
        }

        Assert.Fail(
            $"The dispatcher still had rows to claim after {MaxRounds} batches, which means "
            + "claimed rows are not being settled.");
    }

    /// <summary>
    /// Pushes the fake clock forward until the dispatcher has done the work, or
    /// gives up. The short real delay only yields to the dispatcher's own
    /// asynchronous work; it is not a wait on the schedule, which the fake clock
    /// controls.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(25);
        }

        Assert.Fail("The dispatcher did not publish the seeded message before the timeout.");
    }

    private async Task<List<Guid>> SeedPendingAsync(int count)
    {
        var occurredAt = DateTimeOffset.UtcNow;

        var messages = Enumerable.Range(0, count)
            .Select(index => OutboxMessage.Create(
                nameof(PaymentCreated),
                JsonSerializer.Serialize(
                    new PaymentCreated(Guid.CreateVersion7(), 10.00m, "GBP", occurredAt),
                    PayloadSerializerOptions),
                // Distinct instants keep the oldest-first order well defined.
                occurredAt.AddMilliseconds(index)))
            .ToList();

        await factory.QueryDatabaseAsync(async database =>
        {
            database.OutboxMessages.AddRange(messages);
            return await database.SaveChangesAsync();
        });

        var ids = messages.Select(message => message.Id).ToList();
        _seeded.AddRange(ids);

        return ids;
    }

    private Task<OutboxMessage> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        factory.QueryDatabaseAsync(database => database.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.Id == id, cancellationToken));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await factory.DeleteOutboxMessagesAsync(_seeded);
}
