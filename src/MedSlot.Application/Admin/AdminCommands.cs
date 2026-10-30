using MedSlot.Application.Abstractions;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;

namespace MedSlot.Application.Admin;

/// <summary>
/// Исключение из расписания врача: отпуск, больничный (<c>DayOff</c>) или особые часы (<c>CustomHours</c>).
/// Если на эти даты есть записи вне нового рабочего времени, исключение не добавляется (domain.md, §5).
/// </summary>
public sealed record AddScheduleOverride(
    DoctorId DoctorId,
    LocalDate From,
    LocalDate To,
    ScheduleOverrideKind Kind,
    IReadOnlyList<LocalTimeRange> Hours,
    string? Reason) : ICommand<Unit>;

/// <summary>Убрать исключение: на эти даты снова действует недельное расписание.</summary>
public sealed record RemoveScheduleOverride(DoctorId DoctorId, LocalDate From, LocalDate To) : ICommand<Unit>;

/// <summary>
/// Новое недельное расписание врача. Действует сразу; если активные записи в горизонте записи
/// оказываются вне нового рабочего времени, расписание не меняется (как и для исключений, domain.md, §5).
/// Дни без интервалов — нерабочие.
/// </summary>
public sealed record ChangeWeeklySchedule(
    DoctorId DoctorId, IReadOnlyDictionary<IsoDayOfWeek, IReadOnlyList<LocalTimeRange>> Days) : ICommand<Unit>;

/// <summary>Досрочно снять бан: нарушения до этого момента больше не учитываются (ADR 0004).</summary>
public sealed record LiftPatientBan(PatientId PatientId) : ICommand<Unit>;

internal sealed class AddScheduleOverrideHandler(
    IDoctorRepository doctors, IAppointmentRepository appointments, IUnitOfWork unitOfWork, ClinicPolicy policy)
    : ICommandHandler<AddScheduleOverride, Unit>
{
    public async Task<Result<Unit>> Handle(AddScheduleOverride command, CancellationToken cancellationToken)
    {
        var doctor = await doctors.GetByIdAsync(command.DoctorId, cancellationToken);
        if (doctor is null)
        {
            return ApplicationErrors.NotFound("doctor", command.DoctorId);
        }

        if (command.To < command.From)
        {
            return ApplicationErrors.DateRangeReversed;
        }

        var dates = new DateInterval(command.From, command.To);
        var scheduleOverride = command.Kind == ScheduleOverrideKind.DayOff
            ? ScheduleOverride.DayOff(dates, command.Reason)
            : ScheduleOverride.CustomHours(dates, command.Hours, command.Reason);
        if (scheduleOverride.IsFailure)
        {
            return scheduleOverride.Error;
        }

        var added = doctor.AddScheduleOverride(scheduleOverride.Value);
        if (added.IsFailure)
        {
            return added.Error;
        }

        return await ScheduleChange.SaveIfNoConflictsAsync(doctor, dates, appointments, unitOfWork, policy, cancellationToken);
    }
}

internal sealed class RemoveScheduleOverrideHandler(
    IDoctorRepository doctors, IAppointmentRepository appointments, IUnitOfWork unitOfWork, ClinicPolicy policy)
    : ICommandHandler<RemoveScheduleOverride, Unit>
{
    public async Task<Result<Unit>> Handle(RemoveScheduleOverride command, CancellationToken cancellationToken)
    {
        var doctor = await doctors.GetByIdAsync(command.DoctorId, cancellationToken);
        if (doctor is null)
        {
            return ApplicationErrors.NotFound("doctor", command.DoctorId);
        }

        if (command.To < command.From)
        {
            return ApplicationErrors.DateRangeReversed;
        }

        var dates = new DateInterval(command.From, command.To);
        var removed = doctor.RemoveScheduleOverride(dates);
        if (removed.IsFailure)
        {
            return removed.Error;
        }

        // Особые часы могли быть шире недельных: записи в них не должны оказаться вне расписания.
        return await ScheduleChange.SaveIfNoConflictsAsync(doctor, dates, appointments, unitOfWork, policy, cancellationToken);
    }
}

internal sealed class ChangeWeeklyScheduleHandler(
    IDoctorRepository doctors, IAppointmentRepository appointments, IUnitOfWork unitOfWork, ClinicPolicy policy, IClock clock)
    : ICommandHandler<ChangeWeeklySchedule, Unit>
{
    public async Task<Result<Unit>> Handle(ChangeWeeklySchedule command, CancellationToken cancellationToken)
    {
        var doctor = await doctors.GetByIdAsync(command.DoctorId, cancellationToken);
        if (doctor is null)
        {
            return ApplicationErrors.NotFound("doctor", command.DoctorId);
        }

        var schedule = WeeklySchedule.Create(command.Days
            .Where(d => d.Value.Count > 0)
            .ToDictionary(d => d.Key, d => d.Value.AsEnumerable()));
        if (schedule.IsFailure)
        {
            return schedule.Error;
        }

        doctor.ChangeSchedule(schedule.Value);

        // Записаться дальше горизонта нельзя, а прошедшие записи новое расписание не затрагивает.
        var now = clock.GetCurrentInstant();
        var window = new TimeRange(now, now + policy.BookingHorizon);
        return await ScheduleChange.SaveIfNoConflictsAsync(doctor, window, appointments, unitOfWork, policy, cancellationToken);
    }
}

internal sealed class LiftPatientBanHandler(IPatientRepository patients, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<LiftPatientBan, Unit>
{
    public async Task<Result<Unit>> Handle(LiftPatientBan command, CancellationToken cancellationToken)
    {
        var patient = await patients.GetByIdAsync(command.PatientId, cancellationToken);
        if (patient is null)
        {
            return ApplicationErrors.NotFound("patient", command.PatientId);
        }

        patient.LiftBan(clock.GetCurrentInstant());
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}

internal static class ScheduleChange
{
    /// <summary>
    /// Сохраняет изменённое расписание, только если все активные записи врача в датах изменения
    /// по-прежнему попадают в рабочее время.
    /// </summary>
    public static Task<Result<Unit>> SaveIfNoConflictsAsync(
        Doctor doctor,
        DateInterval dates,
        IAppointmentRepository appointments,
        IUnitOfWork unitOfWork,
        ClinicPolicy policy,
        CancellationToken cancellationToken)
    {
        var window = new TimeRange(
            dates.Start.AtStartOfDayInZone(policy.TimeZone).ToInstant(),
            dates.End.PlusDays(1).AtStartOfDayInZone(policy.TimeZone).ToInstant());
        return SaveIfNoConflictsAsync(doctor, window, appointments, unitOfWork, policy, cancellationToken);
    }

    /// <summary>
    /// Сохраняет изменённое расписание, только если все активные записи врача, пересекающиеся с окном,
    /// по-прежнему попадают в рабочее время.
    /// </summary>
    public static async Task<Result<Unit>> SaveIfNoConflictsAsync(
        Doctor doctor,
        TimeRange window,
        IAppointmentRepository appointments,
        IUnitOfWork unitOfWork,
        ClinicPolicy policy,
        CancellationToken cancellationToken)
    {
        var busy = await appointments.GetBusyRangesAsync(doctor.Id, window, cancellationToken);

        var conflicts = busy.Count(time => !doctor.IsWorkingDuring(time, policy.TimeZone));
        if (conflicts > 0)
        {
            return DoctorErrors.ScheduleConflictsWithAppointments(conflicts);
        }

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}
