namespace MedSlot.Domain.Clinic;

/// <summary>Параметры правил клиники. Значения по умолчанию — domain.md, §2.</summary>
public sealed record ClinicPolicy
{
    public static ClinicPolicy Default { get; } = new();

    /// <summary>Таймзона клиники: в ней задаются расписания.</summary>
    public DateTimeZone TimeZone { get; init; } = DateTimeZoneProviders.Tzdb["Europe/Moscow"];

    /// <summary>Шаг сетки, по которому выравниваются начала слотов.</summary>
    public Duration SlotGridStep { get; init; } = Duration.FromMinutes(15);

    /// <summary>Насколько вперёд можно записаться.</summary>
    public Duration BookingHorizon { get; init; } = Duration.FromDays(60);

    /// <summary>Минимальное время от момента записи до начала приёма.</summary>
    public Duration MinBookingLeadTime { get; init; } = Duration.FromHours(1);

    public int MaxActiveAppointmentsPerPatient { get; init; } = 5;

    /// <summary>Отмена не позже чем за это время до приёма — без последствий (N часов).</summary>
    public Duration FreeCancellationWindow { get; init; } = Duration.FromHours(24);

    /// <summary>Через сколько после окончания приёма неотмеченная запись становится неявкой.</summary>
    public Duration NoShowAutoMarkDelay { get; init; } = Duration.FromHours(2);

    public Duration ViolationWindow { get; init; } = Duration.FromDays(90);

    public int ViolationsForBan { get; init; } = 3;

    public Duration BanDuration { get; init; } = Duration.FromDays(30);

    /// <summary>Время на ответ по предложению из листа ожидания.</summary>
    public Duration WaitlistOfferTtl { get; init; } = Duration.FromMinutes(30);

    public int MaxWaitlistEntriesPerPatient { get; init; } = 3;

    public int MaxExpiredOffersPerEntry { get; init; } = 2;
}
