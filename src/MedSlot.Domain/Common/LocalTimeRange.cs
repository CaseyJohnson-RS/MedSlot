using NodaTime.TimeZones;

namespace MedSlot.Domain.Common;

/// <summary>
/// Интервал времени на часах клиники <c>[From, To)</c> в пределах одних суток.
/// Смены через полночь не поддерживаются (см. domain.md, §11).
/// </summary>
public readonly record struct LocalTimeRange
{
    public LocalTimeRange(LocalTime from, LocalTime to)
    {
        if (to <= from)
        {
            throw new ArgumentException($"Конец интервала ({to}) должен быть позже начала ({from}).", nameof(to));
        }

        From = from;
        To = to;
    }

    public LocalTime From { get; }

    public LocalTime To { get; }

    public bool Overlaps(LocalTimeRange other) => From < other.To && other.From < To;

    /// <summary>
    /// Переводит интервал на конкретную дату в моменты времени (правила — domain.md, §4).
    /// Возвращает <c>null</c>, если интервал целиком попал в пропущенный час перехода на летнее время.
    /// </summary>
    public TimeRange? ToTimeRange(LocalDate date, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var start = date.At(From).InZone(zone, Resolvers.LenientResolver).ToInstant();
        var end = date.At(To).InZone(zone, Resolvers.LenientResolver).ToInstant();
        return end > start ? new TimeRange(start, end) : null;
    }

    public override string ToString() => $"{From:HH:mm}–{To:HH:mm}";
}
