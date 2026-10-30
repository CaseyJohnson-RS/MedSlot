namespace MedSlot.Domain.Appointments;

/// <summary>Статусы записи (domain.md, §6.1).</summary>
public enum AppointmentStatus
{
    /// <summary>Удержание под предложение из листа ожидания.</summary>
    Held,
    Booked,

    /// <summary>Удержание снято: пациент отказался или не ответил вовремя.</summary>
    Released,
    Cancelled,

    /// <summary>Пациент отменил позже чем за <c>FreeCancellationWindow</c>. Считается нарушением.</summary>
    LateCancelled,
    Completed,

    /// <summary>Пациент не пришёл. Считается нарушением.</summary>
    NoShow,
}

public enum BookingSource
{
    Patient,
    Admin,
    Waitlist,
}

public enum CancellationInitiator
{
    Patient,
    Clinic,
}
