using MedSlot.Workers.Common.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NodaTime;

namespace MedSlot.Workers.Notifications;

public enum NotificationKind
{
    BookingConfirmed,
    Reminder,
    Cancelled,
    WaitlistOffer,
}

public enum NotificationStatus
{
    Pending,
    Sent,
    Cancelled,
    Failed,
}

/// <summary>
/// Уведомление, которое нужно отправить в момент <see cref="DueAt"/>. Хранит только идентификаторы:
/// контакты пациента берутся в момент отправки и в БД воркера не копируются.
/// </summary>
public sealed class Notification
{
    public required Guid Id { get; init; }

    /// <summary>Событие, породившее уведомление. Вместе с <see cref="Kind"/> уникально.</summary>
    public required Guid SourceEventId { get; init; }

    public required NotificationKind Kind { get; init; }

    public required Guid AppointmentId { get; init; }

    public required Guid PatientId { get; init; }

    public required Instant StartsAt { get; init; }

    /// <summary>Срок ответа на предложение из листа ожидания.</summary>
    public Instant? RespondUntil { get; init; }

    public required Instant DueAt { get; set; }

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public required Instant CreatedAt { get; init; }

    public Instant? SentAt { get; set; }
}

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : WorkerDbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Notification>(builder =>
        {
            builder.ToTable("notifications");
            builder.HasKey(n => n.Id);
            builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(30);
            builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(20);
            builder.Property(n => n.LastError).HasMaxLength(1000);

            builder.HasIndex(n => new { n.SourceEventId, n.Kind }).IsUnique();
            builder.HasIndex(n => n.AppointmentId);

            // Выборка диспетчера: только ожидающие, по времени отправки.
            builder.HasIndex(n => n.DueAt)
                .HasDatabaseName("ix_notifications_pending_due_at")
                .HasFilter("status = 'Pending'");
        });
    }
}

/// <summary>Для <c>dotnet ef</c>: миграции создаются без запуска воркера.</summary>
internal sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>();
        WorkerDatabase.Configure(options, "Host=localhost;Port=5432;Database=medslot_notifications;Username=medslot;Password=medslot");
        return new NotificationsDbContext(options.Options);
    }
}
