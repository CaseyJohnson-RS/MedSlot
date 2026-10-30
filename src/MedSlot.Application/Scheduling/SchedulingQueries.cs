using MedSlot.Application.Abstractions;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Scheduling;

namespace MedSlot.Application.Scheduling;

public sealed record SlotDto(Instant StartsAt, Instant EndsAt);

/// <summary>Свободные слоты врача для услуги на даты <c>From</c>–<c>To</c> (включительно) по календарю клиники.</summary>
public sealed record GetAvailableSlots(DoctorId DoctorId, ServiceId ServiceId, LocalDate From, LocalDate To)
    : IQuery<IReadOnlyList<SlotDto>>
{
    public const int MaxDays = 31;
}

public sealed record WorkingIntervalDto(LocalTime From, LocalTime To);

public sealed record ScheduleOverrideDto(
    LocalDate From,
    LocalDate To,
    ScheduleOverrideKind Kind,
    IReadOnlyList<WorkingIntervalDto> Hours,
    string? Reason);

public sealed record DoctorScheduleDto(
    DoctorId DoctorId,
    string FullName,
    string Specialization,
    IReadOnlyDictionary<IsoDayOfWeek, IReadOnlyList<WorkingIntervalDto>> Weekly,
    IReadOnlyList<ScheduleOverrideDto> Overrides);

public sealed record GetDoctorSchedule(DoctorId DoctorId) : IQuery<DoctorScheduleDto>;

internal sealed class GetAvailableSlotsHandler(
    IDoctorRepository doctors,
    IServiceRepository services,
    IAppointmentRepository appointments,
    IClock clock,
    ClinicPolicy policy) : IQueryHandler<GetAvailableSlots, IReadOnlyList<SlotDto>>
{
    public async Task<Result<IReadOnlyList<SlotDto>>> Handle(GetAvailableSlots query, CancellationToken cancellationToken)
    {
        if (query.To < query.From || Period.DaysBetween(query.From, query.To) >= GetAvailableSlots.MaxDays)
        {
            return ApplicationErrors.InvalidDateRange(GetAvailableSlots.MaxDays);
        }

        var doctor = await doctors.GetByIdAsync(query.DoctorId, cancellationToken);
        if (doctor is null)
        {
            return ApplicationErrors.NotFound("doctor", query.DoctorId);
        }

        var service = await services.GetByIdAsync(query.ServiceId, cancellationToken);
        if (service is null)
        {
            return ApplicationErrors.NotFound("service", query.ServiceId);
        }

        var window = new TimeRange(
            query.From.AtStartOfDayInZone(policy.TimeZone).ToInstant(),
            query.To.PlusDays(1).AtStartOfDayInZone(policy.TimeZone).ToInstant());
        var busy = await appointments.GetBusyRangesAsync(doctor.Id, window, cancellationToken);

        var slots = SlotCalculator.Calculate(
            doctor, service, new DateInterval(query.From, query.To), busy, clock.GetCurrentInstant(), policy);
        if (slots.IsFailure)
        {
            return slots.Error;
        }

        return Result.Success<IReadOnlyList<SlotDto>>(slots.Value.Select(s => new SlotDto(s.Start, s.End)).ToList());
    }
}

internal sealed class GetDoctorScheduleHandler(IDoctorRepository doctors) : IQueryHandler<GetDoctorSchedule, DoctorScheduleDto>
{
    public async Task<Result<DoctorScheduleDto>> Handle(GetDoctorSchedule query, CancellationToken cancellationToken)
    {
        var doctor = await doctors.GetByIdAsync(query.DoctorId, cancellationToken);
        if (doctor is null)
        {
            return ApplicationErrors.NotFound("doctor", query.DoctorId);
        }

        var weekly = doctor.Schedule.Days
            .OrderBy(d => d.Key)
            .ToDictionary(d => d.Key, d => (IReadOnlyList<WorkingIntervalDto>)ToDto(d.Value));
        var overrides = doctor.Overrides
            .OrderBy(o => o.Dates.Start)
            .Select(o => new ScheduleOverrideDto(o.Dates.Start, o.Dates.End, o.Kind, ToDto(o.Hours), o.Reason))
            .ToList();

        return new DoctorScheduleDto(doctor.Id, doctor.FullName, doctor.Specialization, weekly, overrides);
    }

    private static List<WorkingIntervalDto> ToDto(WorkingHours hours) =>
        hours.Intervals.Select(i => new WorkingIntervalDto(i.From, i.To)).ToList();
}
