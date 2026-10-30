using System.Text.Json;
using MedSlot.Contracts;
using MedSlot.Workers.Common;
using MedSlot.Workers.Common.Consuming;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace MedSlot.Workers.Audit;

/// <summary>Записывает в журнал аудита каждое событие — любого типа и любой версии схемы.</summary>
internal sealed class AuditProcessor(AuditDbContext db, IClock clock) : IIntegrationEventProcessor
{
    public bool CanHandle(string eventType, int schemaVersion) => true;

    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        db.Records.Add(new AuditRecord
        {
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            SchemaVersion = envelope.SchemaVersion,
            AggregateId = envelope.AggregateId,
            DoctorId = envelope.DoctorId,
            OccurredAt = Instant.FromDateTimeOffset(envelope.OccurredAt),
            RecordedAt = clock.GetCurrentInstant(),
            Envelope = JsonSerializer.Serialize(envelope, EventEnvelope.SerializerOptions),
        });
        return Task.CompletedTask;
    }
}

public static class AuditWorker
{
    public const string ConsumerName = "audit";

    public static ConsumerOptions DefaultConsumer(string bootstrapServers) => new()
    {
        BootstrapServers = bootstrapServers,
        GroupId = "medslot-audit",
        ConsumerName = ConsumerName,

        // Аудиту нужна вся история событий, в том числе случившихся до первого запуска.
        StartFromEarliest = true,
    };

    public static IServiceCollection AddAuditWorker(this IServiceCollection services, string connectionString, ConsumerOptions consumer)
    {
        services.AddEventConsumer<AuditDbContext>(connectionString, consumer);
        services.AddScoped<IIntegrationEventProcessor, AuditProcessor>();
        return services;
    }
}
