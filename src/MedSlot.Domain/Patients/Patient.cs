using MedSlot.Domain.Common;

namespace MedSlot.Domain.Patients;

public sealed class Patient : AggregateRoot<PatientId>
{
    // Для EF Core.
    private Patient()
        : this(default, null!, null!, null)
    {
    }

    private Patient(PatientId id, string fullName, PhoneNumber phone, Email? email)
        : base(id)
    {
        FullName = fullName;
        Phone = phone;
        Email = email;
    }

    public string FullName { get; private set; }

    public PhoneNumber Phone { get; private set; }

    public Email? Email { get; private set; }

    /// <summary>Момент досрочного снятия бана: нарушения до него не учитываются (ADR 0004).</summary>
    public Instant? BanLiftedAt { get; private set; }

    public static Result<Patient> Create(PatientId id, string fullName, PhoneNumber phone, Email? email)
    {
        ArgumentNullException.ThrowIfNull(phone);

        if (string.IsNullOrWhiteSpace(fullName))
        {
            return PatientErrors.FullNameRequired;
        }

        return new Patient(id, fullName.Trim(), phone, email);
    }

    public void LiftBan(Instant now) => BanLiftedAt = now;
}
