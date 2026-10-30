using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace MedSlot.Workers.Common.Inbox;

/// <summary>
/// Отметка «событие обработано этим консьюмером» (inbox, ADR 0010). Вставляется в той же транзакции,
/// что и результат обработки, поэтому повторная доставка события не даёт второго эффекта.
/// </summary>
public sealed class ProcessedEvent
{
    public required string Consumer { get; init; }

    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    public required Instant ProcessedAt { get; init; }
}

/// <summary>Базовый контекст БД воркера: у каждого воркера своя база, inbox — в ней же.</summary>
public abstract class WorkerDbContext(DbContextOptions options) : DbContext(options)
{
    public const string InboxPrimaryKey = "pk_processed_events";

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ProcessedEvent>(ConfigureInbox);

    private static void ConfigureInbox(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("processed_events");
        builder.HasKey(e => new { e.Consumer, e.EventId }).HasName(InboxPrimaryKey);
        builder.Property(e => e.Consumer).HasMaxLength(100);
        builder.Property(e => e.EventType).HasMaxLength(100);
    }
}

public static class WorkerDatabase
{
    // Без EnableRetryOnFailure: обработка идёт в явной транзакции, а повторы делает цикл консьюмера.
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention();
}
