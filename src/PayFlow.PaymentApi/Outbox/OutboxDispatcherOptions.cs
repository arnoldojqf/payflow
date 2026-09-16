using System.ComponentModel.DataAnnotations;

namespace PayFlow.PaymentApi.Outbox;

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// How long the dispatcher waits between polls when there is no backlog.
    /// Sets the floor on publish latency, so it is short; a busy dispatcher
    /// keeps draining without waiting for the next tick.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Rows claimed per batch. Each batch holds its row locks until the last
    /// message in it has been published, so a larger batch means fewer round
    /// trips but longer locks.
    /// </summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;
}
