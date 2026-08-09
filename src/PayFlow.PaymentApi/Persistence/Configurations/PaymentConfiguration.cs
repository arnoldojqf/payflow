using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.PaymentApi.Payments;

namespace PayFlow.PaymentApi.Persistence.Configurations;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    /// <summary>
    /// Named explicitly so the create-payment handler can recognise a unique
    /// violation on this specific index rather than assuming it is the only
    /// unique constraint on the table.
    /// </summary>
    public const string IdempotencyKeyIndexName = "ix_payments_idempotency_key";

    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        builder.HasKey(payment => payment.Id);

        builder.Property(payment => payment.IdempotencyKey)
            .IsRequired();

        builder.Property(payment => payment.Amount)
            .HasColumnType("numeric(18,2)");

        // ISO 4217 alphabetic code.
        builder.Property(payment => payment.Currency)
            .IsRequired()
            .HasMaxLength(3);

        builder.Property(payment => payment.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(payment => payment.CreatedAt)
            .IsRequired();

        // The source of truth for idempotency: concurrent retries race here and
        // the database decides the winner.
        builder.HasIndex(payment => payment.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName(IdempotencyKeyIndexName);
    }
}
