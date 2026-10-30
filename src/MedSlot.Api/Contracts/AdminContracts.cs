using MedSlot.Domain.Doctors;
using MedSlot.Domain.Identity;

namespace MedSlot.Api.Contracts;

/// <summary>Запись пациента администратором: без проверки бана и лимита активных записей.</summary>
public sealed record AdminBookAppointmentRequest(Guid PatientId, Guid DoctorId, Guid ServiceId, DateTimeOffset StartsAt);

/// <summary>Учётная запись сотрудника. Для врача нужен <c>DoctorId</c>, для администратора — нет.</summary>
public sealed record CreateStaffRequest(string Email, string Password, UserRole Role, Guid? DoctorId);

public sealed record StaffCreatedResponse(Guid Id);

public sealed record WorkingIntervalRequest(TimeOnly From, TimeOnly To);

/// <summary>Исключение из расписания на даты <c>From</c>–<c>To</c> (включительно, по календарю клиники).</summary>
public sealed record ScheduleOverrideRequest(
    DateOnly From,
    DateOnly To,
    ScheduleOverrideKind Kind,
    IReadOnlyList<WorkingIntervalRequest>? Hours,
    string? Reason);
