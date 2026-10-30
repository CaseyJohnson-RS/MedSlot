using MedSlot.Application.Appointments;
using MedSlot.Application.Scheduling;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Doctors;
using NodaTime;
using NodaTime.Extensions;

namespace MedSlot.Api.Contracts;

// Контракты HTTP API на типах BCL (Guid, DateTimeOffset, DateOnly, TimeOnly): JSON и OpenAPI
// работают с ними из коробки, а внутренние типы (NodaTime, строгие ID) не протекают наружу.
// Моменты времени отдаются в UTC.

/// <summary>Запись пациента: сам пациент берётся из токена.</summary>
public sealed record BookAppointmentRequest(Guid DoctorId, Guid ServiceId, DateTimeOffset StartsAt);

public sealed record CancelByClinicRequest(string ReasonCode);

public sealed record AppointmentCreatedResponse(Guid Id);

public sealed record AppointmentResponse(
    Guid Id,
    Guid DoctorId,
    Guid PatientId,
    Guid ServiceId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    AppointmentStatus Status)
{
    internal static AppointmentResponse Create(AppointmentDto dto) => new(
        dto.Id.Value,
        dto.DoctorId.Value,
        dto.PatientId.Value,
        dto.ServiceId.Value,
        dto.StartsAt.ToDateTimeOffset(),
        dto.EndsAt.ToDateTimeOffset(),
        dto.Status);
}

public sealed record SlotResponse(DateTimeOffset StartsAt, DateTimeOffset EndsAt)
{
    internal static SlotResponse Create(SlotDto dto) => new(dto.StartsAt.ToDateTimeOffset(), dto.EndsAt.ToDateTimeOffset());
}

public sealed record WorkingIntervalResponse(TimeOnly From, TimeOnly To)
{
    internal static WorkingIntervalResponse Create(WorkingIntervalDto dto) => new(dto.From.ToTimeOnly(), dto.To.ToTimeOnly());
}

public sealed record WorkingDayResponse(DayOfWeek Day, IReadOnlyList<WorkingIntervalResponse> Hours);

public sealed record ScheduleOverrideResponse(
    DateOnly From,
    DateOnly To,
    ScheduleOverrideKind Kind,
    IReadOnlyList<WorkingIntervalResponse> Hours,
    string? Reason);

/// <summary>Расписание в локальном времени клиники.</summary>
public sealed record DoctorScheduleResponse(
    Guid DoctorId,
    string FullName,
    string Specialization,
    string TimeZone,
    IReadOnlyList<WorkingDayResponse> Weekly,
    IReadOnlyList<ScheduleOverrideResponse> Overrides)
{
    internal static DoctorScheduleResponse Create(DoctorScheduleDto dto, DateTimeZone zone) => new(
        dto.DoctorId.Value,
        dto.FullName,
        dto.Specialization,
        zone.Id,
        dto.Weekly
            .Select(d => new WorkingDayResponse(d.Key.ToDayOfWeek(), d.Value.Select(WorkingIntervalResponse.Create).ToList()))
            .ToList(),
        dto.Overrides
            .Select(o => new ScheduleOverrideResponse(
                o.From.ToDateOnly(), o.To.ToDateOnly(), o.Kind, o.Hours.Select(WorkingIntervalResponse.Create).ToList(), o.Reason))
            .ToList());
}
