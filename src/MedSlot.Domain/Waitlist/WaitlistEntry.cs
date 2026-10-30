using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;

namespace MedSlot.Domain.Waitlist;

public enum WaitlistEntryStatus
{
    /// <summary>В очереди, ждёт освободившегося времени.</summary>
    Active,

    /// <summary>Пациенту предложено время, ждём ответа.</summary>
    Offered,

    /// <summary>Пациент принял предложение — записан.</summary>
    Fulfilled,

    Cancelled,

    /// <summary>Диапазон дат прошёл или пациент несколько раз не ответил на предложение.</summary>
    Expired,
}

/// <summary>Заявка в листе ожидания (domain.md, §8).</summary>
public sealed class WaitlistEntry : AggregateRoot<WaitlistEntryId>
{
    // Для EF Core.
    private WaitlistEntry()
        : base(default)
    {
    }

    private WaitlistEntry(
        WaitlistEntryId id, PatientId patientId, DoctorId doctorId, ServiceId serviceId, LocalDate from, LocalDate to, Instant now)
        : base(id)
    {
        PatientId = patientId;
        DoctorId = doctorId;
        ServiceId = serviceId;
        From = from;
        To = to;
        CreatedAt = now;
        QueuedAt = now;
        Status = WaitlistEntryStatus.Active;
    }

    public PatientId PatientId { get; }

    public DoctorId DoctorId { get; }

    public ServiceId ServiceId { get; }

    /// <summary>Первая дата, на которую пациент готов записаться (по календарю клиники).</summary>
    public LocalDate From { get; }

    /// <summary>Последняя дата, включительно.</summary>
    public LocalDate To { get; }

    public DateInterval Dates => new(From, To);

    public Instant CreatedAt { get; }

    /// <summary>
    /// Позиция в очереди: чем раньше, тем раньше предложение. После отказа или пропущенного
    /// предложения заявка уходит в конец очереди, чтобы то же время не предлагалось ей снова первой.
    /// </summary>
    public Instant QueuedAt { get; private set; }

    public WaitlistEntryStatus Status { get; private set; }

    public int ExpiredOffersCount { get; private set; }

    /// <summary>Удержание (запись в статусе Held), предложенное по заявке сейчас.</summary>
    public AppointmentId? CurrentOfferId { get; private set; }

    /// <summary>Заявка ещё может получить или ждёт предложение.</summary>
    public bool IsOpen => Status is WaitlistEntryStatus.Active or WaitlistEntryStatus.Offered;

    public static Result<WaitlistEntry> Join(
        WaitlistEntryId id,
        PatientId patientId,
        DoctorId doctorId,
        ServiceId serviceId,
        DateInterval dates,
        Instant now,
        ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(policy);

        var today = now.InZone(policy.TimeZone).Date;
        if (dates.Start < today)
        {
            return WaitlistErrors.DatesInPast;
        }

        var lastBookable = (now + policy.BookingHorizon).InZone(policy.TimeZone).Date;
        if (dates.Start > lastBookable)
        {
            return WaitlistErrors.DatesBeyondHorizon;
        }

        return new WaitlistEntry(id, patientId, doctorId, serviceId, dates.Start, dates.End, now);
    }

    /// <summary>Подходит ли заявка под освободившийся интервал: дата начала в диапазоне и услуга помещается.</summary>
    public bool Fits(TimeRange freed, Duration serviceDuration, DateTimeZone zone) =>
        Status == WaitlistEntryStatus.Active
        && Dates.Contains(freed.Start.InZone(zone).Date)
        && serviceDuration <= freed.Duration;

    public Result MarkOffered(AppointmentId offerId)
    {
        if (Status != WaitlistEntryStatus.Active)
        {
            return WaitlistErrors.InvalidStatus(Status, "предложить время");
        }

        Status = WaitlistEntryStatus.Offered;
        CurrentOfferId = offerId;
        return Result.Success();
    }

    public Result Fulfill()
    {
        if (Status != WaitlistEntryStatus.Offered)
        {
            return WaitlistErrors.InvalidStatus(Status, "принять предложение");
        }

        Status = WaitlistEntryStatus.Fulfilled;
        return Result.Success();
    }

    /// <summary>Пациент отказался от предложенного времени: заявка остаётся, но уходит в конец очереди.</summary>
    public Result OfferDeclined(Instant now)
    {
        if (Status != WaitlistEntryStatus.Offered)
        {
            return WaitlistErrors.InvalidStatus(Status, "отказаться от предложения");
        }

        BackToQueue(now);
        return Result.Success();
    }

    /// <summary>
    /// Пациент не ответил вовремя. После <c>MaxExpiredOffersPerEntry</c> пропущенных предложений
    /// заявка закрывается: пациент, похоже, больше не ждёт.
    /// </summary>
    public Result OfferExpired(Instant now, ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (Status != WaitlistEntryStatus.Offered)
        {
            return WaitlistErrors.InvalidStatus(Status, "снять истёкшее предложение");
        }

        ExpiredOffersCount++;
        if (ExpiredOffersCount >= policy.MaxExpiredOffersPerEntry)
        {
            Status = WaitlistEntryStatus.Expired;
            CurrentOfferId = null;
        }
        else
        {
            BackToQueue(now);
        }

        return Result.Success();
    }

    /// <summary>Пациент отменил заявку. Если по ней есть предложение, его снимает вызывающий код.</summary>
    public Result Cancel()
    {
        if (!IsOpen)
        {
            return WaitlistErrors.InvalidStatus(Status, "отменить заявку");
        }

        Status = WaitlistEntryStatus.Cancelled;
        return Result.Success();
    }

    private void BackToQueue(Instant now)
    {
        Status = WaitlistEntryStatus.Active;
        CurrentOfferId = null;
        QueuedAt = now;
    }
}
