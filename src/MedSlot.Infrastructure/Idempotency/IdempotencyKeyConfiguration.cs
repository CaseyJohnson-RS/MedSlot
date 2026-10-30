using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSlot.Infrastructure.Idempotency;

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    /// <summary>Имя первичного ключа: по нему UnitOfWork узнаёт параллельный запрос с тем же ключом.</summary>
    public const string PrimaryKeyName = "pk_idempotency_keys";

    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(k => new { k.Scope, k.Key }).HasName(PrimaryKeyName);

        builder.Property(k => k.Scope).HasMaxLength(100);
        builder.Property(k => k.Key).HasMaxLength(IdempotencyLimits.MaxKeyLength);
        builder.Property(k => k.RequestHash).HasMaxLength(64).IsFixedLength();
        builder.Property(k => k.ResponseContentType).HasMaxLength(100);
        builder.Property(k => k.ResponseLocation).HasMaxLength(500);

        // Для очистки устаревших ключей.
        builder.HasIndex(k => k.CreatedAt);
    }
}

public static class IdempotencyLimits
{
    public const int MaxKeyLength = 100;

    /// <summary>Сколько хранится ключ: повтор позже считается новым запросом.</summary>
    public static readonly NodaTime.Duration Retention = NodaTime.Duration.FromHours(24);
}
