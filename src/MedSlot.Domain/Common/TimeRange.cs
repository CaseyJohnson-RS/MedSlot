namespace MedSlot.Domain.Common;

/// <summary>Полуоткрытый интервал моментов времени <c>[Start, End)</c>.</summary>
public readonly record struct TimeRange
{
    public TimeRange(Instant start, Instant end)
    {
        if (end <= start)
        {
            throw new ArgumentException($"Конец интервала ({end}) должен быть позже начала ({start}).", nameof(end));
        }

        Start = start;
        End = end;
    }

    public Instant Start { get; }

    public Instant End { get; }

    public Duration Duration => End - Start;

    public static TimeRange FromStart(Instant start, Duration duration) => new(start, start + duration);

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public bool Contains(TimeRange other) => Start <= other.Start && other.End <= End;

    public override string ToString() => $"[{Start}, {End})";
}
