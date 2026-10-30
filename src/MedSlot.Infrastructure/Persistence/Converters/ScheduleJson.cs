using System.Text.Json;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using NodaTime;
using NodaTime.Text;

namespace MedSlot.Infrastructure.Persistence.Converters;

/// <summary>
/// Формат расписания в jsonb. Отдельные DTO, а не сериализация доменных классов:
/// формат в БД не должен меняться от рефакторинга домена.
/// </summary>
internal static class ScheduleJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly LocalTimePattern TimePattern = LocalTimePattern.CreateWithInvariantCulture("HH:mm");
    private static readonly LocalDatePattern DatePattern = LocalDatePattern.Iso;

    public static string SerializeWeekly(WeeklySchedule schedule) =>
        JsonSerializer.Serialize(
            schedule.Days.ToDictionary(d => d.Key.ToString(), d => ToJson(d.Value)),
            Options);

    public static WeeklySchedule DeserializeWeekly(string json)
    {
        var days = JsonSerializer.Deserialize<Dictionary<string, List<IntervalJson>>>(json, Options) ?? [];
        return WeeklySchedule.Create(days.ToDictionary(
            d => Enum.Parse<IsoDayOfWeek>(d.Key),
            d => d.Value.Select(FromJson))).Value;
    }

    public static string SerializeOverrides(List<ScheduleOverride> overrides) =>
        JsonSerializer.Serialize(
            overrides.Select(o => new OverrideJson(
                DatePattern.Format(o.Dates.Start),
                DatePattern.Format(o.Dates.End),
                o.Kind.ToString(),
                ToJson(o.Hours),
                o.Reason)),
            Options);

    public static List<ScheduleOverride> DeserializeOverrides(string json) =>
        (JsonSerializer.Deserialize<List<OverrideJson>>(json, Options) ?? [])
            .Select(o =>
            {
                var dates = new DateInterval(DatePattern.Parse(o.From).Value, DatePattern.Parse(o.To).Value);
                return Enum.Parse<ScheduleOverrideKind>(o.Kind) == ScheduleOverrideKind.DayOff
                    ? ScheduleOverride.DayOff(dates, o.Reason)
                    : ScheduleOverride.CustomHours(dates, o.Hours.Select(FromJson), o.Reason).Value;
            })
            .ToList();

    private static List<IntervalJson> ToJson(WorkingHours hours) =>
        hours.Intervals.Select(i => new IntervalJson(TimePattern.Format(i.From), TimePattern.Format(i.To))).ToList();

    private static LocalTimeRange FromJson(IntervalJson interval) =>
        new(TimePattern.Parse(interval.From).Value, TimePattern.Parse(interval.To).Value);

    private sealed record IntervalJson(string From, string To);

    private sealed record OverrideJson(string From, string To, string Kind, List<IntervalJson> Hours, string? Reason);
}
