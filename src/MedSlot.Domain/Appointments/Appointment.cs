using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;

namespace MedSlot.Domain.Appointments;

/// <summary>
/// Запись пациента к врачу. Переходы — domain.md, §6.2.
/// Отсутствие пересечений с другими записями гарантирует exclusion constraint в БД, а не агрегат.
/// </summary>
public sealed class Appointment : AggregateRoot<AppointmentId>
{
    private Appointment(
        AppointmentId id,
        DoctorId doctorId,
        PatientId patientId,
        ServiceId serviceId,
        TimeRange time,
        AppointmentStatus status,
        BookingSource bookedBy,
        Instant now)
        : base(id)
    {
        DoctorId = doctorId;
        PatientId = patientId;
        ServiceId = serviceId;
        Time = time;
        Status = status;
        BookedBy = bookedBy;
        StatusChangedAt = now;
    }

    public DoctorId DoctorId { get; }

    public PatientId PatientId { get; }

    public ServiceId ServiceId { get; }

    public TimeRange Time { get; }

    public AppointmentStatus Status { get; private set; }

    /// <summary>Момент последней смены статуса. Для нарушений — момент нарушения (ADR 0004).</summary>
    public Instant StatusChangedAt { get; private set; }

    public BookingSource BookedBy { get; private set; }

    public WaitlistEntryId? WaitlistEntryId { get; private set; }

    public Instant? HoldExpiresAt { get; private set; }

    public CancellationInitiator? CancelledBy { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Активная запись занимает время врача и пациента.</summary>
    public bool IsActive => Status is AppointmentStatus.Held or AppointmentStatus.Booked;

    public bool IsViolation => Status is AppointmentStatus.LateCancelled or AppointmentStatus.NoShow;

    /// <summary>Запись пациентом или администратором.</summary>
    public static Result<Appointment> Book(
        AppointmentId id,
        DoctorId doctorId,
        PatientId patientId,
        ServiceId serviceId,
        TimeRange time,
        BookingSource source,
        Instant now,
        ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (source == BookingSource.Waitlist)
        {
            throw new ArgumentException("Запись из листа ожидания создаётся через Hold и AcceptOffer.", nameof(source));
        }

        if (time.Start < now + policy.MinBookingLeadTime)
        {
            return AppointmentErrors.StartTooSoon;
        }

        if (time.Start > now + policy.BookingHorizon)
        {
            return AppointmentErrors.BeyondBookingHorizon;
        }

        var appointment = new Appointment(id, doctorId, patientId, serviceId, time, AppointmentStatus.Booked, source, now);
        appointment.RaiseBooked(now);
        return appointment;
    }

    /// <summary>Удержание интервала под предложение из листа ожидания (domain.md, §8).</summary>
    public static Result<Appointment> Hold(
        AppointmentId id,
        WaitlistEntryId waitlistEntryId,
        DoctorId doctorId,
        PatientId patientId,
        ServiceId serviceId,
        TimeRange time,
        Instant now,
        ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Пациент должен успеть ответить, и после ответа запись должна пройти по MinBookingLeadTime.
        if (time.Start - now < policy.WaitlistOfferTtl + policy.MinBookingLeadTime)
        {
            return AppointmentErrors.OfferTooLate;
        }

        var appointment = new Appointment(
            id, doctorId, patientId, serviceId, time, AppointmentStatus.Held, BookingSource.Waitlist, now)
        {
            WaitlistEntryId = waitlistEntryId,
            HoldExpiresAt = now + policy.WaitlistOfferTtl,
        };

        appointment.Raise(new WaitlistSlotOffered(
            waitlistEntryId, id, doctorId, patientId, time.Start, time.End, appointment.HoldExpiresAt.Value, now));
        return appointment;
    }

    public Result AcceptOffer(Instant now)
    {
        if (Status != AppointmentStatus.Held)
        {
            return AppointmentErrors.InvalidStatus(Status, "принять предложение");
        }

        if (now >= HoldExpiresAt)
        {
            return AppointmentErrors.OfferExpired;
        }

        ChangeStatus(AppointmentStatus.Booked, now);
        HoldExpiresAt = null;
        Raise(new WaitlistOfferAccepted(WaitlistEntryId!.Value, Id, DoctorId, PatientId, now));
        RaiseBooked(now);
        return Result.Success();
    }

    public Result DeclineOffer(Instant now)
    {
        if (Status != AppointmentStatus.Held)
        {
            return AppointmentErrors.InvalidStatus(Status, "отказаться от предложения");
        }

        ChangeStatus(AppointmentStatus.Released, now);
        Raise(new WaitlistOfferDeclined(WaitlistEntryId!.Value, Id, DoctorId, PatientId, now));
        return Result.Success();
    }

    public Result ExpireOffer(Instant now)
    {
        if (Status != AppointmentStatus.Held)
        {
            return AppointmentErrors.InvalidStatus(Status, "снять истёкшее предложение");
        }

        if (now < HoldExpiresAt)
        {
            return AppointmentErrors.OfferNotExpiredYet;
        }

        ChangeStatus(AppointmentStatus.Released, now);
        Raise(new WaitlistOfferExpired(WaitlistEntryId!.Value, Id, DoctorId, PatientId, now));
        return Result.Success();
    }

    /// <summary>
    /// Отмена пациентом. Не позже чем за <c>FreeCancellationWindow</c> (граница включительно) —
    /// <see cref="AppointmentStatus.Cancelled"/>, позже — <see cref="AppointmentStatus.LateCancelled"/>.
    /// </summary>
    public Result CancelByPatient(Instant now, ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var check = EnsureBookedAndNotStarted(now, "отменить запись");
        if (check.IsFailure)
        {
            return check;
        }

        var isLate = now > Time.Start - policy.FreeCancellationWindow;
        ChangeStatus(isLate ? AppointmentStatus.LateCancelled : AppointmentStatus.Cancelled, now);
        CancelledBy = CancellationInitiator.Patient;
        Raise(new AppointmentCancelled(
            Id, DoctorId, PatientId, Time.Start, CancellationInitiator.Patient, isLate, null, now));
        return Result.Success();
    }

    /// <summary>Отмена врачом или администратором. Никогда не считается нарушением пациента.</summary>
    public Result CancelByClinic(Instant now, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return AppointmentErrors.CancellationReasonRequired;
        }

        var check = EnsureBookedAndNotStarted(now, "отменить запись");
        if (check.IsFailure)
        {
            return check;
        }

        ChangeStatus(AppointmentStatus.Cancelled, now);
        CancelledBy = CancellationInitiator.Clinic;
        CancellationReason = reasonCode;
        Raise(new AppointmentCancelled(
            Id, DoctorId, PatientId, Time.Start, CancellationInitiator.Clinic, false, reasonCode, now));
        return Result.Success();
    }

    public Result Complete(Instant now)
    {
        var check = EnsureBookedAndStarted(now, "завершить приём");
        if (check.IsFailure)
        {
            return check;
        }

        ChangeStatus(AppointmentStatus.Completed, now);
        Raise(new AppointmentCompleted(Id, DoctorId, PatientId, now));
        return Result.Success();
    }

    /// <summary>Неявку отмечает врач после начала приёма.</summary>
    public Result MarkNoShow(Instant now)
    {
        var check = EnsureBookedAndStarted(now, "отметить неявку");
        if (check.IsFailure)
        {
            return check;
        }

        ChangeStatus(AppointmentStatus.NoShow, now);
        Raise(new AppointmentNoShow(Id, DoctorId, PatientId, false, now));
        return Result.Success();
    }

    /// <summary>Автоматическая неявка, если врач не отметил приём через <c>NoShowAutoMarkDelay</c> после окончания.</summary>
    public Result AutoMarkNoShow(Instant now, ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (Status != AppointmentStatus.Booked)
        {
            return AppointmentErrors.InvalidStatus(Status, "автоматически отметить неявку");
        }

        if (now < Time.End + policy.NoShowAutoMarkDelay)
        {
            return AppointmentErrors.AutoNoShowTooEarly;
        }

        ChangeStatus(AppointmentStatus.NoShow, now);
        Raise(new AppointmentNoShow(Id, DoctorId, PatientId, true, now));
        return Result.Success();
    }

    private Result EnsureBookedAndNotStarted(Instant now, string action)
    {
        if (Status != AppointmentStatus.Booked)
        {
            return AppointmentErrors.InvalidStatus(Status, action);
        }

        return now >= Time.Start ? AppointmentErrors.AlreadyStarted : Result.Success();
    }

    private Result EnsureBookedAndStarted(Instant now, string action)
    {
        if (Status != AppointmentStatus.Booked)
        {
            return AppointmentErrors.InvalidStatus(Status, action);
        }

        return now < Time.Start ? AppointmentErrors.NotStartedYet : Result.Success();
    }

    private void ChangeStatus(AppointmentStatus status, Instant now)
    {
        Status = status;
        StatusChangedAt = now;
    }

    private void RaiseBooked(Instant now) =>
        Raise(new AppointmentBooked(Id, DoctorId, PatientId, ServiceId, Time.Start, Time.End, BookedBy, now));
}
