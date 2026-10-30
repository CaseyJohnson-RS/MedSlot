using MedSlot.Contracts;
using MedSlot.Workers.Common.Consuming;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace MedSlot.Workers.Notifications;

/// <summary>
/// Превращает события в запланированные уведомления. Ничего не отправляет сам:
/// работает в транзакции с inbox, а отправку делает <see cref="NotificationDispatcher"/>.
/// </summary>
internal sealed class NotificationsProcessor(NotificationsDbContext db, NotificationsSettings settings, IClock clock)
    : IIntegrationEventProcessor
{
    public bool CanHandle(string eventType, int schemaVersion) =>
        schemaVersion == 1 && eventType is EventTypes.AppointmentBooked or EventTypes.AppointmentCancelled or EventTypes.WaitlistSlotOffered;

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        switch (envelope.EventType)
        {
            case EventTypes.AppointmentBooked:
                {
                    var e = envelope.PayloadAs<AppointmentBookedV1>();
                    var startsAt = Instant.FromDateTimeOffset(e.StartsAt);
                    Add(envelope, NotificationKind.BookingConfirmed, e.AppointmentId, e.PatientId, startsAt, dueAt: now);

                    // Напоминание — только если до приёма больше суток; иначе подтверждения достаточно.
                    var remindAt = startsAt - settings.ReminderLeadTime;
                    if (remindAt > now)
                    {
                        Add(envelope, NotificationKind.Reminder, e.AppointmentId, e.PatientId, startsAt, dueAt: remindAt);
                    }

                    break;
                }

            case EventTypes.AppointmentCancelled:
                {
                    var e = envelope.PayloadAs<AppointmentCancelledV1>();

                    // Отменённый приём не напоминаем. Выполняется в транзакции консьюмера.
                    await db.Notifications
                        .Where(n => n.AppointmentId == e.AppointmentId
                            && n.Kind == NotificationKind.Reminder
                            && n.Status == NotificationStatus.Pending)
                        .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NotificationStatus.Cancelled), cancellationToken);

                    Add(envelope, NotificationKind.Cancelled, e.AppointmentId, e.PatientId, Instant.FromDateTimeOffset(e.StartsAt), dueAt: now);
                    break;
                }

            case EventTypes.WaitlistSlotOffered:
                {
                    var e = envelope.PayloadAs<WaitlistSlotOfferedV1>();
                    Add(
                        envelope,
                        NotificationKind.WaitlistOffer,
                        e.AppointmentId,
                        e.PatientId,
                        Instant.FromDateTimeOffset(e.StartsAt),
                        dueAt: now,
                        respondUntil: Instant.FromDateTimeOffset(e.ExpiresAt));
                    break;
                }
        }

        void Add(
            EventEnvelope source, NotificationKind kind, Guid appointmentId, Guid patientId, Instant startsAt, Instant dueAt, Instant? respondUntil = null) =>
            db.Notifications.Add(new Notification
            {
                Id = Guid.CreateVersion7(),
                SourceEventId = source.EventId,
                Kind = kind,
                AppointmentId = appointmentId,
                PatientId = patientId,
                StartsAt = startsAt,
                RespondUntil = respondUntil,
                DueAt = dueAt,
                CreatedAt = now,
            });
    }
}
