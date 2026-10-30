using MedSlot.Domain.Common;

namespace MedSlot.Domain.Doctors;

public enum ScheduleOverrideKind
{
    /// <summary>Нет приёма: отпуск, больничный.</summary>
    DayOff,

    /// <summary>Свои часы вместо недельного расписания.</summary>
    CustomHours,
}

/// <summary>Отклонение от недельного расписания на диапазон дат (включительно).</summary>
public sealed class ScheduleOverride
{
    private ScheduleOverride(DateInterval dates, ScheduleOverrideKind kind, WorkingHours hours, string? reason)
    {
        Dates = dates;
        Kind = kind;
        Hours = hours;
        Reason = reason;
    }

    public DateInterval Dates { get; }

    public ScheduleOverrideKind Kind { get; }

    /// <summary>Рабочие часы на каждый день диапазона; пусто для <see cref="ScheduleOverrideKind.DayOff"/>.</summary>
    public WorkingHours Hours { get; }

    public string? Reason { get; }

    public static ScheduleOverride DayOff(DateInterval dates, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(dates);
        return new ScheduleOverride(dates, ScheduleOverrideKind.DayOff, WorkingHours.None, reason);
    }

    public static Result<ScheduleOverride> CustomHours(
        DateInterval dates,
        IEnumerable<LocalTimeRange> intervals,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(dates);

        var hours = WorkingHours.Create(intervals);
        if (hours.IsFailure)
        {
            return hours.Error;
        }

        if (hours.Value.Intervals.Count == 0)
        {
            return DoctorErrors.CustomHoursEmpty;
        }

        return new ScheduleOverride(dates, ScheduleOverrideKind.CustomHours, hours.Value, reason);
    }

    public bool Overlaps(ScheduleOverride other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Dates.Start <= other.Dates.End && other.Dates.Start <= Dates.End;
    }
}
