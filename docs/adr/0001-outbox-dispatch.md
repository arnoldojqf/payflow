# 1. Dispatching the transactional outbox

- Status: Accepted
- Date: 2026-09-16

## Context

The write path stores a `PaymentCreated` row in `OutboxMessages` in the same
transaction as the payment, so the event cannot be lost once the payment is
accepted. Something then has to move those rows to Azure Service Bus.

Two properties matter more than throughput here:

1. **No lost events.** A payment that was accepted must eventually produce a
   published event, whatever crashes in between.
2. **No double publishing by accident.** The API will run as more than one
   replica on AKS, and every replica will run a dispatcher.

Publishing inside the HTTP request was rejected when the outbox was introduced:
it would make accepting a payment depend on the broker being reachable, which is
the coupling the outbox exists to remove.

## Decision

A `BackgroundService` in the Payment API polls with a `PeriodicTimer` and, on
each tick, runs batches of:

```sql
SELECT ... FROM "OutboxMessages"
WHERE "ProcessedAt" IS NULL
ORDER BY "OccurredAt"
LIMIT @BatchSize
FOR UPDATE SKIP LOCKED
```

Every message in the batch is published, then the successful rows are stamped
with `ProcessedAt` and the failed ones have `Attempts` incremented — all inside
the transaction that claimed them.

### Why FOR UPDATE SKIP LOCKED

`FOR UPDATE` takes a row lock on each claimed row that is held until the
transaction ends. `SKIP LOCKED` tells a second dispatcher to step over rows that
are already locked instead of waiting behind them.

Together they make the claim exclusive without a coordinator, a leader election,
or a distributed lock. Two replicas polling the same table take disjoint batches
and neither blocks the other.

Plain `FOR UPDATE` would be safe but not concurrent: the second dispatcher would
block on the first dispatcher's rows for as long as it takes to publish them,
serialising every replica behind whichever one is slowest.

No locking at all would be neither. Both dispatchers would read the same pending
rows in their own snapshots and publish every one of them twice.

The `WHERE` and `ORDER BY` match the `(ProcessedAt, OccurredAt)` index, so a
claim seeks straight to the pending rows and stops at the `LIMIT`.

### Why the publish sits inside the transaction

The alternative is to commit the claim first, publish afterwards, and mark the
rows in a second transaction. That shortens the lock but opens a window where a
crash leaves rows claimed by a process that no longer exists and marked by
nothing — they would need a lease and a reaper to become visible again.

Keeping the publish inside the claim means a crash rolls the claim back and the
rows are simply pending again. The cost is that row locks are held for the
duration of a batch's network calls, which is why `BatchSize` is small.

## Consequences

### Delivery is at-least-once, and consumers must be idempotent

The broker can acknowledge a message and the transaction can then fail to
commit — a crash, a dropped connection, a rolled-back batch. Those rows are
still pending, so the next tick publishes them again.

This cannot be fixed by reordering the steps. Committing before publishing
trades duplicates for lost events, which is the worse failure for a payment
system. There is no atomic commit across Postgres and Service Bus short of a
distributed transaction, which neither side supports here.

So duplicates are a normal event, not an incident:

- Every message carries the **outbox row id as its `MessageId`**, stable across
  republishes, so Service Bus duplicate detection and consumers can both
  recognise the second copy.
- **Consumers must be idempotent.** The Payment Processor and Webhook
  Dispatcher must both treat a repeated `PaymentCreated` as a no-op rather than
  as a second payment. This is a requirement on those services, not a detail of
  this one.

### Other consequences

- A batch that does not publish in full stops the drain loop and waits for the
  next tick, so a broker outage cannot become a hot retry loop.
- Publish failures are logged and counted in `Attempts`, but nothing yet stops
  retrying a message that can never succeed. Capping attempts and dead-lettering
  poison messages is deliberately left to a later change.
- Dispatch failures never propagate out of `ExecuteAsync`, which would otherwise
  stop the host: the API keeps accepting payments while the broker is down, and
  the backlog drains when it returns.
- Polling costs one indexed query per second per replica when idle. If that
  latency floor ever matters, `LISTEN/NOTIFY` can wake the dispatcher sooner
  without changing the claim logic.
