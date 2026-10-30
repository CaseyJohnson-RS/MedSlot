using System.Text.Json;
using MedSlot.Contracts;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using NodaTime;

namespace MedSlot.Infrastructure.Outbox;

/// <summary>
/// Доменное событие → интеграционное (контракт из <c>MedSlot.Contracts</c>).
/// Отдельное отображение, а не сериализация доменных типов: контракт не меняется от рефакторинга домена.
/// </summary>
internal static class IntegrationEventMapper
{
    private const int SchemaVersion = 1;

    public static OutboxMessage ToOutboxMessage(IDomainEvent domainEvent, string? traceParent)
    {
        var (eventType, aggregateId, doctorId, payload) = Map(domainEvent);

        var envelope = new EventEnvelope(
            Guid.CreateVersion7(),
            eventType,
            SchemaVersion,
            domainEvent.OccurredAt.ToDateTimeOffset(),
            aggregateId.Value,
            doctorId.Value,
            traceParent,
            JsonSerializer.SerializeToElement(payload, payload.GetType(), EventEnvelope.SerializerOptions));

        return new OutboxMessage
        {
            EventId = envelope.EventId,
            EventType = eventType,
            PartitionKey = doctorId.Value.ToString(),
            Envelope = JsonSerializer.Serialize(envelope, EventEnvelope.SerializerOptions),
            TraceParent = traceParent,
            OccurredAt = domainEvent.OccurredAt,
        };
    }

    private static (string Type, AppointmentId AggregateId, DoctorId DoctorId, object Payload) Map(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            AppointmentBooked e => (EventTypes.AppointmentBooked, e.AppointmentId, e.DoctorId, new AppointmentBookedV1(
                e.AppointmentId.Value, e.DoctorId.Value, e.PatientId.Value, e.ServiceId.Value,
                e.StartsAt.ToDateTimeOffset(), e.EndsAt.ToDateTimeOffset(), e.BookedBy.ToString())),

            AppointmentCancelled e => (EventTypes.AppointmentCancelled, e.AppointmentId, e.DoctorId, new AppointmentCancelledV1(
                e.AppointmentId.Value, e.DoctorId.Value, e.PatientId.Value, e.StartsAt.ToDateTimeOffset(),
                e.InitiatedBy.ToString(), e.IsLate, e.ReasonCode)),

            AppointmentCompleted e => (EventTypes.AppointmentCompleted, e.AppointmentId, e.DoctorId,
                new AppointmentCompletedV1(e.AppointmentId.Value, e.DoctorId.Value, e.PatientId.Value)),

            AppointmentNoShow e => (EventTypes.AppointmentNoShow, e.AppointmentId, e.DoctorId,
                new AppointmentNoShowV1(e.AppointmentId.Value, e.DoctorId.Value, e.PatientId.Value, e.Auto)),

            WaitlistSlotOffered e => (EventTypes.WaitlistSlotOffered, e.AppointmentId, e.DoctorId, new WaitlistSlotOfferedV1(
                e.WaitlistEntryId.Value, e.AppointmentId.Value, e.DoctorId.Value, e.PatientId.Value,
                e.StartsAt.ToDateTimeOffset(), e.EndsAt.ToDateTimeOffset(), e.ExpiresAt.ToDateTimeOffset())),

            WaitlistOfferAccepted e => Answered(EventTypes.WaitlistOfferAccepted, e.WaitlistEntryId, e.AppointmentId, e.DoctorId, e.PatientId),
            WaitlistOfferDeclined e => Answered(EventTypes.WaitlistOfferDeclined, e.WaitlistEntryId, e.AppointmentId, e.DoctorId, e.PatientId),
            WaitlistOfferExpired e => Answered(EventTypes.WaitlistOfferExpired, e.WaitlistEntryId, e.AppointmentId, e.DoctorId, e.PatientId),

            // Новое доменное событие без отображения — ошибка разработки, а не повод молча его потерять.
            _ => throw new InvalidOperationException($"Нет интеграционного события для {domainEvent.GetType().Name}."),
        };

    private static (string, AppointmentId, DoctorId, object) Answered(
        string type, WaitlistEntryId entryId, AppointmentId appointmentId, DoctorId doctorId, PatientId patientId) =>
        (type, appointmentId, doctorId, new WaitlistOfferAnsweredV1(entryId.Value, appointmentId.Value, doctorId.Value, patientId.Value));
}
