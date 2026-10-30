using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Services;

namespace MedSlot.Domain.Tests;

internal static class TestData
{
    public static readonly DateTimeZone Moscow = DateTimeZoneProviders.Tzdb["Europe/Moscow"];

    /// <summary>Зона с переходами на летнее время: 29.03.2026 02:00→03:00, 25.10.2026 03:00→02:00.</summary>
    public static readonly DateTimeZone Berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];

    public static readonly ClinicPolicy Policy = ClinicPolicy.Default;

    /// <summary>Понедельник.</summary>
    public static readonly LocalDate Monday = new(2026, 11, 2);

    public static Instant Msk(LocalDate date, int hour, int minute = 0, int second = 0) =>
        date.At(new LocalTime(hour, minute, second)).InZoneStrictly(Moscow).ToInstant();

    public static LocalTimeRange Hours(int fromHour, int fromMinute, int toHour, int toMinute) =>
        new(new LocalTime(fromHour, fromMinute), new LocalTime(toHour, toMinute));

    public static WeeklySchedule Weekdays(params LocalTimeRange[] hours)
    {
        var days = new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday }
            .ToDictionary(d => d, _ => (IEnumerable<LocalTimeRange>)hours);
        return WeeklySchedule.Create(days).Value;
    }

    public static Service NewService(int minutes, ClinicPolicy? policy = null) =>
        Domain.Services.Service.Create(ServiceId.New(), $"Приём {minutes} мин", Duration.FromMinutes(minutes), policy ?? Policy).Value;

    public static Doctor NewDoctor(WeeklySchedule schedule, params Service[] services)
    {
        var doctor = Doctor.Create(DoctorId.New(), "Иванов Иван Иванович", "Терапевт", schedule).Value;
        foreach (var service in services)
        {
            doctor.AddService(service.Id);
        }

        return doctor;
    }
}
