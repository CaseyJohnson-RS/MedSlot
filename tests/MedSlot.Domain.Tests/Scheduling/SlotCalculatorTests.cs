using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Scheduling;
using static MedSlot.Domain.Tests.TestData;

namespace MedSlot.Domain.Tests.Scheduling;

public class SlotCalculatorTests
{
    // Неделей раньше понедельника: горизонт и минимальное время до записи не мешают.
    private static readonly Instant Now = Msk(Monday.PlusDays(-7), 12);

    private static IReadOnlyList<TimeRange> Slots(
        Doctor doctor,
        Services.Service service,
        IEnumerable<TimeRange>? busy = null,
        Instant? now = null,
        DateInterval? dates = null,
        Clinic.ClinicPolicy? policy = null) =>
        SlotCalculator.Calculate(
            doctor,
            service,
            dates ?? new DateInterval(Monday, Monday),
            busy ?? [],
            now ?? Now,
            policy ?? Policy).Value;

    private static IEnumerable<string> StartTimes(IEnumerable<TimeRange> slots, DateTimeZone? zone = null) =>
        slots.Select(s => s.Start.InZone(zone ?? Moscow).ToString("HH:mm", null));

    [Fact]
    public void Slots_are_aligned_to_grid_and_fit_working_hours()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 10, 0)), service);

        StartTimes(Slots(doctor, service)).ShouldBe(["09:00", "09:15", "09:30"]);
    }

    [Fact]
    public void Service_that_does_not_fit_before_break_or_end_of_day_is_skipped()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 9, 45), Hours(10, 0, 10, 30)), service);

        StartTimes(Slots(doctor, service)).ShouldBe(["09:00", "09:15", "10:00"]);
    }

    [Fact]
    public void Busy_intervals_are_excluded()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 11, 0)), service);
        var busy = new TimeRange(Msk(Monday, 9, 30), Msk(Monday, 10, 0));

        StartTimes(Slots(doctor, service, [busy])).ShouldBe(["09:00", "10:00", "10:15", "10:30"]);
    }

    [Fact]
    public void Short_busy_interval_blocks_every_overlapping_long_slot()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 10, 0)), service);
        var busy = TimeRange.FromStart(Msk(Monday, 9, 15), Duration.FromMinutes(15));

        StartTimes(Slots(doctor, service, [busy])).ShouldBe(["09:30"]);
    }

    [Fact]
    public void Day_off_has_no_slots()
    {
        var service = NewService(15);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 18, 0)), service);
        doctor.AddScheduleOverride(ScheduleOverride.DayOff(new DateInterval(Monday, Monday)));

        Slots(doctor, service).ShouldBeEmpty();
    }

    [Fact]
    public void Custom_hours_are_used_instead_of_weekly_schedule()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 18, 0)), service);
        doctor.AddScheduleOverride(ScheduleOverride.CustomHours(new DateInterval(Monday, Monday), [Hours(15, 0, 16, 0)]).Value);

        StartTimes(Slots(doctor, service)).ShouldBe(["15:00", "15:15", "15:30"]);
    }

    [Fact]
    public void Slots_cover_every_date_in_range()
    {
        var service = NewService(60);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 10, 0)), service);

        // Пн–Вс: пять рабочих дней по одному слоту.
        Slots(doctor, service, dates: new DateInterval(Monday, Monday.PlusDays(6))).Count.ShouldBe(5);
    }

    [Fact]
    public void Slots_earlier_than_min_lead_time_are_skipped()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 11, 0)), service);
        var now = Msk(Monday, 8, 30); // + 1 ч → ближайшее начало 09:30

        StartTimes(Slots(doctor, service, now: now)).ShouldBe(["09:30", "09:45", "10:00", "10:15", "10:30"]);
    }

    [Fact]
    public void Slots_beyond_booking_horizon_are_skipped()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 11, 0)), service);
        var policy = Policy with { BookingHorizon = Duration.FromHours(2), MinBookingLeadTime = Duration.Zero };

        // now = 08:00, горизонт 2 ч → начало не позже 10:00 (граница включительно).
        StartTimes(Slots(doctor, service, now: Msk(Monday, 8, 0), policy: policy))
            .ShouldBe(["09:00", "09:15", "09:30", "09:45", "10:00"]);
    }

    [Fact]
    public void Doctor_must_provide_the_service()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 18, 0)));

        var result = SlotCalculator.Calculate(doctor, NewService(30), new DateInterval(Monday, Monday), [], Now, Policy);

        result.Error.Code.ShouldBe("doctor.service_not_provided");
    }

    [Fact]
    public void Spring_forward_day_has_slots_only_on_real_time_axis()
    {
        var policy = Policy with { TimeZone = Berlin };
        var service = NewService(30);
        var doctor = NewDoctor(
            WeeklySchedule.Create(new Dictionary<IsoDayOfWeek, IEnumerable<LocalTimeRange>>
            {
                [IsoDayOfWeek.Sunday] = [Hours(1, 0, 5, 0)],
            }).Value,
            service);
        var day = new LocalDate(2026, 3, 29);

        var slots = Slots(doctor, service, now: Instant.FromUtc(2026, 3, 20, 0, 0), dates: new DateInterval(day, day), policy: policy);

        // 01:00 CET → 05:00 CEST = 3 часа реального времени: (180 − 30) / 15 + 1 = 11 слотов.
        slots.Count.ShouldBe(11);
        StartTimes(slots, Berlin).ShouldNotContain(t => t.StartsWith("02:", StringComparison.Ordinal));
        slots.ShouldAllBe(s => s.Duration == Duration.FromMinutes(30));
    }

    [Theory]
    [InlineData(9, 0, true)]
    [InlineData(9, 30, true)] // последний, который помещается до 10:00
    [InlineData(9, 45, false)] // не помещается
    [InlineData(9, 7, false)] // не по сетке
    [InlineData(8, 45, false)] // до начала смены
    public void Is_within_schedule(int hour, int minute, bool expected)
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 10, 0)), service);

        SlotCalculator.IsWithinSchedule(doctor, service, Msk(Monday, hour, minute), Policy).ShouldBe(expected);
    }

    [Fact]
    public void Is_within_schedule_ignores_busy_and_lead_time()
    {
        var service = NewService(30);
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 10, 0)), service);

        // Даже если «сейчас» — после приёма, проверяется только сетка расписания.
        SlotCalculator.IsWithinSchedule(doctor, service, Msk(Monday, 9, 0), Policy).ShouldBeTrue();
        SlotCalculator.IsWithinSchedule(doctor, service, Msk(Monday.PlusDays(5), 9, 0), Policy).ShouldBeFalse(); // суббота
    }

    [Fact]
    public void Working_interval_inside_skipped_hour_is_ignored()
    {
        var policy = Policy with { TimeZone = Berlin };
        var service = NewService(30);
        var doctor = NewDoctor(
            WeeklySchedule.Create(new Dictionary<IsoDayOfWeek, IEnumerable<LocalTimeRange>>
            {
                [IsoDayOfWeek.Sunday] = [Hours(2, 30, 3, 0), Hours(9, 0, 9, 30)],
            }).Value,
            service);
        var day = new LocalDate(2026, 3, 29);

        var slots = Slots(doctor, service, now: Instant.FromUtc(2026, 3, 20, 0, 0), dates: new DateInterval(day, day), policy: policy);

        StartTimes(slots, Berlin).ShouldBe(["09:00"]);
    }

    [Fact]
    public void Fall_back_day_has_extra_hour_of_slots()
    {
        var policy = Policy with { TimeZone = Berlin };
        var service = NewService(30);
        var doctor = NewDoctor(
            WeeklySchedule.Create(new Dictionary<IsoDayOfWeek, IEnumerable<LocalTimeRange>>
            {
                [IsoDayOfWeek.Sunday] = [Hours(1, 0, 5, 0)],
            }).Value,
            service);
        var day = new LocalDate(2026, 10, 25);

        var slots = Slots(doctor, service, now: Instant.FromUtc(2026, 10, 20, 0, 0), dates: new DateInterval(day, day), policy: policy);

        // 01:00 CEST → 05:00 CET = 5 часов: (300 − 30) / 15 + 1 = 19 слотов, 02:xx встречается дважды.
        slots.Count.ShouldBe(19);
        StartTimes(slots, Berlin).Count(t => t == "02:15").ShouldBe(2);
    }
}
