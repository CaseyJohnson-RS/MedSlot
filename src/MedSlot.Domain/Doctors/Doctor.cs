using MedSlot.Domain.Common;

namespace MedSlot.Domain.Doctors;

public sealed class Doctor : AggregateRoot<DoctorId>
{
    private readonly List<ScheduleOverride> _overrides = [];
    private readonly HashSet<ServiceId> _services = [];

    // Для EF Core.
    private Doctor()
        : this(default, null!, null!, null!)
    {
    }

    private Doctor(DoctorId id, string fullName, string specialization, WeeklySchedule schedule)
        : base(id)
    {
        FullName = fullName;
        Specialization = specialization;
        Schedule = schedule;
    }

    public string FullName { get; private set; }

    public string Specialization { get; private set; }

    public WeeklySchedule Schedule { get; private set; }

    public IReadOnlyCollection<ScheduleOverride> Overrides => _overrides;

    public IReadOnlyCollection<ServiceId> Services => _services;

    public static Result<Doctor> Create(DoctorId id, string fullName, string specialization, WeeklySchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (string.IsNullOrWhiteSpace(fullName))
        {
            return DoctorErrors.FullNameRequired;
        }

        if (string.IsNullOrWhiteSpace(specialization))
        {
            return DoctorErrors.SpecializationRequired;
        }

        return new Doctor(id, fullName.Trim(), specialization.Trim(), schedule);
    }

    public void ChangeSchedule(WeeklySchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        Schedule = schedule;
    }

    /// <summary>
    /// Добавляет исключение из расписания. Конфликт с уже существующими записями проверяет
    /// прикладной слой: записи — другой агрегат (domain.md, §5).
    /// </summary>
    public Result AddScheduleOverride(ScheduleOverride scheduleOverride)
    {
        ArgumentNullException.ThrowIfNull(scheduleOverride);

        if (_overrides.Any(o => o.Overlaps(scheduleOverride)))
        {
            return DoctorErrors.OverlappingScheduleOverride(scheduleOverride.Dates);
        }

        _overrides.Add(scheduleOverride);
        return Result.Success();
    }

    public Result RemoveScheduleOverride(DateInterval dates)
    {
        ArgumentNullException.ThrowIfNull(dates);

        var removed = _overrides.RemoveAll(e => e.Dates.Equals(dates));
        return removed > 0 ? Result.Success() : DoctorErrors.ScheduleOverrideNotFound(dates);
    }

    public void AddService(ServiceId serviceId) => _services.Add(serviceId);

    public void RemoveService(ServiceId serviceId) => _services.Remove(serviceId);

    public bool Provides(ServiceId serviceId) => _services.Contains(serviceId);

    /// <summary>Целиком ли интервал попадает в рабочие часы врача (с учётом исключений).</summary>
    public bool IsWorkingDuring(TimeRange time, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // Смены через полночь не поддерживаются: интервал относится к дате своего начала.
        var date = time.Start.InZone(zone).Date;
        return WorkingHoursOn(date).Intervals
            .Select(i => i.ToTimeRange(date, zone))
            .Any(working => working is { } w && w.Contains(time));
    }

    /// <summary>Рабочие часы на дату: исключение, если оно есть, иначе недельное расписание.</summary>
    public WorkingHours WorkingHoursOn(LocalDate date)
    {
        var scheduleOverride = _overrides.FirstOrDefault(o => o.Dates.Contains(date));
        return scheduleOverride?.Hours ?? Schedule.For(date.DayOfWeek);
    }
}
