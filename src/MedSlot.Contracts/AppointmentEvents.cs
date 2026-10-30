namespace MedSlot.Contracts;

// Схемы payload событий топика appointments (domain.md, §9), версия 1.
// Перечисления — строками: консьюмеры не зависят от внутренних enum домена.
// Совместимое изменение схемы — только добавление необязательных полей; иначе новая SchemaVersion.

public static class EventTypes
{
    public const string AppointmentBooked = nameof(AppointmentBooked);
    public const string AppointmentCancelled = nameof(AppointmentCancelled);
    public const string AppointmentCompleted = nameof(AppointmentCompleted);
    public const string AppointmentNoShow = nameof(AppointmentNoShow);
    public const string WaitlistSlotOffered = nameof(WaitlistSlotOffered);
    public const string WaitlistOfferAccepted = nameof(WaitlistOfferAccepted);
    public const string WaitlistOfferDeclined = nameof(WaitlistOfferDeclined);
    public const string WaitlistOfferExpired = nameof(WaitlistOfferExpired);
}

/// <param name="BookedBy"><c>Patient</c>, <c>Admin</c> или <c>Waitlist</c>.</param>
public sealed record AppointmentBookedV1(
    Guid AppointmentId,
    Guid DoctorId,
    Guid PatientId,
    Guid ServiceId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string BookedBy);

/// <param name="InitiatedBy"><c>Patient</c> или <c>Clinic</c>.</param>
/// <param name="IsLate">Отмена позже допустимого срока — нарушение пациента.</param>
public sealed record AppointmentCancelledV1(
    Guid AppointmentId,
    Guid DoctorId,
    Guid PatientId,
    DateTimeOffset StartsAt,
    string InitiatedBy,
    bool IsLate,
    string? ReasonCode);

public sealed record AppointmentCompletedV1(Guid AppointmentId, Guid DoctorId, Guid PatientId);

/// <param name="Auto">Неявка отмечена автоматически, а не врачом.</param>
public sealed record AppointmentNoShowV1(Guid AppointmentId, Guid DoctorId, Guid PatientId, bool Auto);

public sealed record WaitlistSlotOfferedV1(
    Guid WaitlistEntryId,
    Guid AppointmentId,
    Guid DoctorId,
    Guid PatientId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset ExpiresAt);

public sealed record WaitlistOfferAnsweredV1(Guid WaitlistEntryId, Guid AppointmentId, Guid DoctorId, Guid PatientId);
