using System.Text.Json;

namespace MedSlot.Contracts;

/// <summary>
/// Конверт события в Kafka (значение сообщения, JSON). Ключ сообщения — <see cref="DoctorId"/>:
/// события одного врача попадают в одну партицию и читаются по порядку (ADR 0009).
/// </summary>
/// <param name="EventId">Уникальный id события: по нему консьюмеры отбрасывают повторы.</param>
/// <param name="EventType">Имя события, например <c>AppointmentBooked</c> (см. <see cref="EventTypes"/>).</param>
/// <param name="SchemaVersion">Версия схемы <see cref="Payload"/> для этого типа события.</param>
/// <param name="OccurredAt">Когда событие произошло (UTC).</param>
/// <param name="AggregateId">Id записи, с которой произошло событие.</param>
/// <param name="DoctorId">Врач: ключ партиционирования.</param>
/// <param name="TraceParent">W3C traceparent запроса, породившего событие.</param>
/// <param name="Payload">Данные события. Только идентификаторы и время — без персональных данных.</param>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid AggregateId,
    Guid DoctorId,
    string? TraceParent,
    JsonElement Payload)
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    public T PayloadAs<T>() =>
        Payload.Deserialize<T>(SerializerOptions)
        ?? throw new JsonException($"Пустой payload события {EventType} {EventId}.");
}

public static class Topics
{
    public const string Appointments = "appointments";

    /// <summary>Сообщения, которые консьюмер не смог обработать после всех попыток.</summary>
    public const string AppointmentsDeadLetter = "appointments.dlt";
}

/// <summary>Заголовки Kafka-сообщения: дублируют поля конверта для маршрутизации без разбора JSON.</summary>
public static class EventHeaders
{
    public const string EventId = "event-id";
    public const string EventType = "event-type";
    public const string SchemaVersion = "schema-version";
    public const string TraceParent = "traceparent";
}
