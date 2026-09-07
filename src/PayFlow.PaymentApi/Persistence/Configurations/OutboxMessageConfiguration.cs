using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.PaymentApi.Outbox;

namespace PayFlow.PaymentApi.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string PendingIndexName = "ix_outbox_messages_processed_at_occurred_at";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Type)
            .IsRequired()
            .HasMaxLength(200);

        // jsonb rather than text: it lets the payload be queried and indexed by
        // its contents, which text would reduce to string matching.
        builder.Property(message => message.Payload)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(message => message.OccurredAt)
            .IsRequired();

        builder.Property(message => message.Attempts)
            .IsRequired()
            .HasDefaultValue(0);

        // Serves the dispatcher's "fetch pending, oldest first" query:
        // WHERE ProcessedAt IS NULL ORDER BY OccurredAt LIMIT n.
        //
        // ProcessedAt leads because it carries the predicate: Postgres can seek
        // straight to the NULL group instead of walking dispatched history. Within
        // that group the entries are already ordered by OccurredAt, so the ORDER BY
        // needs no sort and the LIMIT stops the scan early. Reversing the columns
        // would leave the leading column unconstrained, forcing a scan of the whole
        // index. General shape: equality/IS NULL columns first, ordering columns after.
        //
        // A partial index (WHERE ProcessedAt IS NULL) would be smaller still, since
        // dispatched rows come to dominate the table; that is a tuning decision to
        // make alongside the dispatcher, against a real query plan.
        builder.HasIndex(message => new { message.ProcessedAt, message.OccurredAt })
            .HasDatabaseName(PendingIndexName);
    }
}
