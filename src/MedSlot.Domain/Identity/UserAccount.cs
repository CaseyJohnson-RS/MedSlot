using MedSlot.Domain.Common;
using MedSlot.Domain.Patients;

namespace MedSlot.Domain.Identity;

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum UserRole
{
    Patient,
    Doctor,
    Admin,
}

/// <summary>
/// Учётная запись для входа. Связана с пациентом или врачом, от чьего имени действует.
/// Пароль хранится только в виде хэша; сам хэш вычисляет инфраструктура.
/// </summary>
public sealed class UserAccount : AggregateRoot<UserId>
{
    // Для EF Core.
    private UserAccount()
        : base(default)
    {
        Email = null!;
        PasswordHash = null!;
    }

    private UserAccount(UserId id, Email email, string passwordHash, UserRole role, PatientId? patientId, DoctorId? doctorId, Instant now)
        : base(id)
    {
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        PatientId = patientId;
        DoctorId = doctorId;
        CreatedAt = now;
    }

    /// <summary>Логин. Может не совпадать с контактным email пациента.</summary>
    public Email Email { get; private set; }

    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    /// <summary>Пациент, от имени которого действует учётная запись (только для роли Patient).</summary>
    public PatientId? PatientId { get; private set; }

    /// <summary>Врач, от имени которого действует учётная запись (только для роли Doctor).</summary>
    public DoctorId? DoctorId { get; private set; }

    public Instant CreatedAt { get; private set; }

    public static UserAccount ForPatient(UserId id, Email email, string passwordHash, PatientId patientId, Instant now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new UserAccount(id, email, passwordHash, UserRole.Patient, patientId, null, now);
    }

    /// <summary>Учётная запись сотрудника: врачу нужен <paramref name="doctorId"/>, администратору — нет.</summary>
    public static Result<UserAccount> ForStaff(
        UserId id, Email email, string passwordHash, UserRole role, DoctorId? doctorId, Instant now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        return role switch
        {
            UserRole.Doctor when doctorId is null => IdentityErrors.DoctorRequired,
            UserRole.Doctor => new UserAccount(id, email, passwordHash, role, null, doctorId, now),
            UserRole.Admin when doctorId is not null => IdentityErrors.AdminCannotBeDoctor,
            UserRole.Admin => new UserAccount(id, email, passwordHash, role, null, null, now),
            _ => IdentityErrors.NotAStaffRole,
        };
    }

    /// <summary>Хэш пересчитан с более сильными параметрами (при входе со старым хэшем).</summary>
    public void ReplacePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }
}
