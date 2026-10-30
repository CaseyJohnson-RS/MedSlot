using MedSlot.Domain.Common;

namespace MedSlot.Domain.Appointments;

public static class AppointmentErrors
{
    public static readonly DomainError StartTooSoon =
        new("appointment.start_too_soon", "Слишком поздно записываться на это время.");

    public static readonly DomainError BeyondBookingHorizon =
        new("appointment.beyond_booking_horizon", "На это время запись ещё не открыта.");

    public static readonly DomainError OfferTooLate =
        new("appointment.offer_too_late", "До приёма слишком мало времени, чтобы предлагать слот из листа ожидания.");

    public static readonly DomainError OfferExpired =
        new("appointment.offer_expired", "Срок ответа на предложение истёк.");

    public static readonly DomainError OfferNotExpiredYet =
        new("appointment.offer_not_expired_yet", "Срок ответа на предложение ещё не истёк.");

    public static readonly DomainError AlreadyStarted =
        new("appointment.already_started", "Приём уже начался.");

    public static readonly DomainError NotStartedYet =
        new("appointment.not_started_yet", "Приём ещё не начался.");

    public static readonly DomainError AutoNoShowTooEarly =
        new("appointment.auto_no_show_too_early", "Ещё рано автоматически отмечать неявку.");

    public static readonly DomainError CancellationReasonRequired =
        new("appointment.cancellation_reason_required", "Для отмены клиникой нужна причина.");

    public static DomainError InvalidStatus(AppointmentStatus current, string action) =>
        new("appointment.invalid_status", $"Нельзя выполнить «{action}» для записи в статусе {current}.");
}
