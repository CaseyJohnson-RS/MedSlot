using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace MedSlot.Infrastructure.Outbox;

/// <summary>Событие, ожидающее публикации в Kafka (ADR 0008).</summary>
internal sealed class OutboxMessage
{
    /// <summary>Порядковый номер: relay публикует строго в порядке возрастания.</summary>
    public long Id { get; init; }

    public required Guid EventId { get; init; }

    public required string EventType { get; init; }

    /// <summary>Ключ Kafka-сообщения — id врача.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Сериализованный <see cref="Contracts.EventEnvelope"/>.</summary>
    public required string Envelope { get; init; }

    public string? TraceParent { get; init; }

    public required Instant OccurredAt { get; init; }

    public Instant? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).UseIdentityAlwaysColumn();

        builder.Property(m => m.EventType).HasMaxLength(100);
        builder.Property(m => m.PartitionKey).HasMaxLength(100);
        builder.Property(m => m.Envelope).HasColumnType("jsonb");
        builder.Property(m => m.TraceParent).HasMaxLength(100);
        builder.Property(m => m.LastError).HasMaxLength(2000);

        builder.HasIndex(m => m.EventId).IsUnique();

        // Выборка relay: только неопубликованные, по порядку. Частичный индекс остаётся маленьким.
        builder.HasIndex(m => m.Id)
            .HasDatabaseName("ix_outbox_messages_unpublished")
            .HasFilter("published_at IS NULL");

        // Очистка опубликованных.
        builder.HasIndex(m => m.PublishedAt);
    }
}
