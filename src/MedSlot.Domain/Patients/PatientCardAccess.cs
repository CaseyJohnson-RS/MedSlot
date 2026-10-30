using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;

namespace MedSlot.Domain.Patients;

public readonly record struct CardAccessId(Guid Value)
{
    public static CardAccessId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum CardAccessorKind
{
    Doctor,
    Admin,

    /// <summary>Внутренний сервис (например, уведомления), а не человек.</summary>
    Service,
}

/// <summary>
/// Запись журнала доступа к карточке пациента (ADR 0012): кто, когда и зачем прочитал персональные данные.
/// Создаётся в одной транзакции с чтением и больше не меняется.
/// </summary>
public sealed class PatientCardAccess : AggregateRoot<CardAccessId>
{
    public const int MaxPurposeLength = 200;

    // Для EF Core.
    private PatientCardAccess()
        : base(default)
    {
        Purpose = null!;
    }

    private PatientCardAccess(
        PatientId patientId, CardAccessorKind kind, UserId? userId, DoctorId? doctorId, string? serviceName, string purpose, Instant now)
        : base(CardAccessId.New())
    {
        PatientId = patientId;
        AccessorKind = kind;
        AccessorUserId = userId;
        DoctorId = doctorId;
        ServiceName = serviceName;
        Purpose = purpose;
        AccessedAt = now;
    }

    public PatientId PatientId { get; private set; }

    public CardAccessorKind AccessorKind { get; private set; }

    /// <summary>Учётная запись сотрудника; для сервиса — <c>null</c>.</summary>
    public UserId? AccessorUserId { get; private set; }

    public DoctorId? DoctorId { get; private set; }

    public string? ServiceName { get; private set; }

    /// <summary>Зачем понадобилась карточка, например <c>appointment</c> или <c>notification</c>.</summary>
    public string Purpose { get; private set; }

    public Instant AccessedAt { get; private set; }

    public static Result<PatientCardAccess> ByDoctor(PatientId patientId, UserId userId, DoctorId doctorId, string purpose, Instant now) =>
        Create(patientId, CardAccessorKind.Doctor, userId, doctorId, null, purpose, now);

    public static Result<PatientCardAccess> ByAdmin(PatientId patientId, UserId userId, string purpose, Instant now) =>
        Create(patientId, CardAccessorKind.Admin, userId, null, null, purpose, now);

    public static Result<PatientCardAccess> ByService(PatientId patientId, string serviceName, string purpose, Instant now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return Create(patientId, CardAccessorKind.Service, null, null, serviceName, purpose, now);
    }

    private static Result<PatientCardAccess> Create(
        PatientId patientId, CardAccessorKind kind, UserId? userId, DoctorId? doctorId, string? serviceName, string purpose, Instant now)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > MaxPurposeLength)
        {
            return PatientErrors.CardAccessPurposeRequired;
        }

        return new PatientCardAccess(patientId, kind, userId, doctorId, serviceName, purpose.Trim(), now);
    }
}
