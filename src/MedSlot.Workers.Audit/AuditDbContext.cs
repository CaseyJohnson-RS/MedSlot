using MedSlot.Workers.Common.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NodaTime;

namespace MedSlot.Workers.Audit;

/// <summary>Запись журнала аудита: событие как есть. Журнал только пополняется (триггер в миграции).</summary>
public sealed class AuditRecord
{
    public long Id { get; init; }

    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    public required int SchemaVersion { get; init; }

    public required Guid AggregateId { get; init; }

    public required Guid DoctorId { get; init; }

    public required Instant OccurredAt { get; init; }

    public required Instant RecordedAt { get; init; }

    /// <summary>Исходный конверт события целиком.</summary>
    public required string Envelope { get; init; }
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : WorkerDbContext(options)
{
    public DbSet<AuditRecord> Records => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AuditRecord>(builder =>
        {
            builder.ToTable("audit_log");
            builder.HasKey(r => r.Id);
            builder.Property(r => r.Id).UseIdentityAlwaysColumn();
            builder.Property(r => r.EventType).HasMaxLength(100);
            builder.Property(r => r.Envelope).HasColumnType("jsonb");
            builder.HasIndex(r => r.EventId).IsUnique();
            builder.HasIndex(r => new { r.AggregateId, r.OccurredAt });
            builder.HasIndex(r => new { r.DoctorId, r.OccurredAt });
        });
    }
}

/// <summary>Для <c>dotnet ef</c>: миграции создаются без запуска воркера.</summary>
internal sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        WorkerDatabase.Configure(options, "Host=localhost;Port=5432;Database=medslot_audit;Username=medslot;Password=medslot");
        return new AuditDbContext(options.Options);
    }
}
