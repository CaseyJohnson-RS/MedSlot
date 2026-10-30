namespace MedSlot.Domain.Common;

// Строго типизированные идентификаторы: DoctorId нельзя по ошибке передать вместо PatientId.
// Guid v7 упорядочен по времени создания — хорошо ложится в B-tree индексы.

public readonly record struct DoctorId(Guid Value)
{
    public static DoctorId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct PatientId(Guid Value)
{
    public static PatientId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct ServiceId(Guid Value)
{
    public static ServiceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct AppointmentId(Guid Value)
{
    public static AppointmentId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct WaitlistEntryId(Guid Value)
{
    public static WaitlistEntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
