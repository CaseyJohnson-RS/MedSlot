using MedSlot.Domain.Common;

namespace MedSlot.Domain.Doctors;

/// <summary>Рабочие интервалы одного дня: отсортированы и не пересекаются.</summary>
public sealed class WorkingHours
{
    private WorkingHours(IReadOnlyList<LocalTimeRange> intervals) => Intervals = intervals;

    public static WorkingHours None { get; } = new([]);

    public IReadOnlyList<LocalTimeRange> Intervals { get; }

    public static Result<WorkingHours> Create(IEnumerable<LocalTimeRange> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var sorted = intervals.OrderBy(i => i.From).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i - 1].Overlaps(sorted[i]))
            {
                return DoctorErrors.OverlappingWorkingHours(sorted[i - 1], sorted[i]);
            }
        }

        return new WorkingHours(sorted);
    }
}
