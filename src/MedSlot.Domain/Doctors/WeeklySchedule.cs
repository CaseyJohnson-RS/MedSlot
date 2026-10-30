using MedSlot.Domain.Common;

namespace MedSlot.Domain.Doctors;

/// <summary>Недельное расписание врача в локальном времени клиники.</summary>
public sealed class WeeklySchedule
{
    private readonly IReadOnlyDictionary<IsoDayOfWeek, WorkingHours> _days;

    private WeeklySchedule(IReadOnlyDictionary<IsoDayOfWeek, WorkingHours> days) => _days = days;

    public static WeeklySchedule Empty { get; } = new(new Dictionary<IsoDayOfWeek, WorkingHours>());

    public static Result<WeeklySchedule> Create(IReadOnlyDictionary<IsoDayOfWeek, IEnumerable<LocalTimeRange>> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        var result = new Dictionary<IsoDayOfWeek, WorkingHours>();
        foreach (var (day, intervals) in days)
        {
            var hours = WorkingHours.Create(intervals);
            if (hours.IsFailure)
            {
                return hours.Error;
            }

            result[day] = hours.Value;
        }

        return new WeeklySchedule(result);
    }

    public WorkingHours For(IsoDayOfWeek day) => _days.GetValueOrDefault(day, WorkingHours.None);
}
