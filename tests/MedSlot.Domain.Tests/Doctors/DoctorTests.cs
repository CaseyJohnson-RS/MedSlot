using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Services;
using static MedSlot.Domain.Tests.TestData;

namespace MedSlot.Domain.Tests.Doctors;

public class DoctorTests
{
    [Theory]
    [InlineData("", "Терапевт", "doctor.full_name_required")]
    [InlineData("Иванов И. И.", " ", "doctor.specialization_required")]
    public void Create_requires_name_and_specialization(string name, string specialization, string code)
    {
        var result = Doctor.Create(DoctorId.New(), name, specialization, WeeklySchedule.Empty);

        result.Error.Code.ShouldBe(code);
    }

    [Fact]
    public void Create_trims_values()
    {
        var doctor = Doctor.Create(DoctorId.New(), "  Иванов И. И. ", " Терапевт ", WeeklySchedule.Empty).Value;

        doctor.FullName.ShouldBe("Иванов И. И.");
        doctor.Specialization.ShouldBe("Терапевт");
    }

    [Fact]
    public void Working_hours_cannot_overlap()
    {
        var result = WeeklySchedule.Create(new Dictionary<IsoDayOfWeek, IEnumerable<LocalTimeRange>>
        {
            [IsoDayOfWeek.Monday] = [Hours(9, 0, 13, 0), Hours(12, 0, 18, 0)],
        });

        result.Error.Code.ShouldBe("doctor.overlapping_working_hours");
    }

    [Fact]
    public void Working_hours_are_sorted()
    {
        var hours = WorkingHours.Create([Hours(14, 0, 18, 0), Hours(9, 0, 13, 0)]).Value;

        hours.Intervals.Select(i => i.From.Hour).ShouldBe([9, 14]);
    }

    [Fact]
    public void Working_hours_on_date_come_from_weekly_schedule()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 13, 0)));

        doctor.WorkingHoursOn(Monday).Intervals.ShouldBe([Hours(9, 0, 13, 0)]);
        doctor.WorkingHoursOn(Monday.PlusDays(5)).Intervals.ShouldBeEmpty(); // суббота
    }

    [Fact]
    public void Day_off_override_replaces_schedule()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 13, 0)));

        doctor.AddScheduleOverride(ScheduleOverride.DayOff(new DateInterval(Monday, Monday.PlusDays(1)), "Отпуск")).IsSuccess.ShouldBeTrue();

        doctor.WorkingHoursOn(Monday).Intervals.ShouldBeEmpty();
        doctor.WorkingHoursOn(Monday.PlusDays(1)).Intervals.ShouldBeEmpty();
        doctor.WorkingHoursOn(Monday.PlusDays(2)).Intervals.ShouldNotBeEmpty();
    }

    [Fact]
    public void Custom_hours_override_replaces_schedule()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 13, 0)));
        var custom = ScheduleOverride.CustomHours(new DateInterval(Monday, Monday), [Hours(15, 0, 17, 0)]).Value;

        doctor.AddScheduleOverride(custom);

        doctor.WorkingHoursOn(Monday).Intervals.ShouldBe([Hours(15, 0, 17, 0)]);
        custom.Kind.ShouldBe(ScheduleOverrideKind.CustomHours);
    }

    [Fact]
    public void Custom_hours_must_be_valid()
    {
        var dates = new DateInterval(Monday, Monday);

        ScheduleOverride.CustomHours(dates, []).Error.Code.ShouldBe("doctor.custom_hours_empty");
        ScheduleOverride.CustomHours(dates, [Hours(9, 0, 12, 0), Hours(11, 0, 13, 0)])
            .Error.Code.ShouldBe("doctor.overlapping_working_hours");
    }

    [Fact]
    public void Overrides_cannot_overlap()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 13, 0)));
        doctor.AddScheduleOverride(ScheduleOverride.DayOff(new DateInterval(Monday, Monday.PlusDays(4))));

        var result = doctor.AddScheduleOverride(ScheduleOverride.DayOff(new DateInterval(Monday.PlusDays(4), Monday.PlusDays(6))));

        result.Error.Code.ShouldBe("doctor.overlapping_schedule_override");
        doctor.Overrides.Count.ShouldBe(1);
    }

    [Fact]
    public void Override_can_be_removed()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 13, 0)));
        var dates = new DateInterval(Monday, Monday);
        doctor.AddScheduleOverride(ScheduleOverride.DayOff(dates));

        doctor.RemoveScheduleOverride(dates).IsSuccess.ShouldBeTrue();
        doctor.RemoveScheduleOverride(dates).Error.Code.ShouldBe("doctor.schedule_override_not_found");
        doctor.WorkingHoursOn(Monday).Intervals.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(9, 0, 30, true)]
    [InlineData(12, 30, 30, true)] // встык с концом интервала
    [InlineData(12, 45, 30, false)] // заходит на перерыв
    [InlineData(13, 15, 15, false)] // в перерыве
    [InlineData(14, 0, 60, true)]
    public void Is_working_during(int hour, int minute, int minutes, bool expected)
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 13, 0), Hours(14, 0, 18, 0)));
        var time = TimeRange.FromStart(Msk(Monday, hour, minute), Duration.FromMinutes(minutes));

        doctor.IsWorkingDuring(time, Moscow).ShouldBe(expected);
    }

    [Fact]
    public void Is_working_during_respects_overrides()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 18, 0)));
        var time = TimeRange.FromStart(Msk(Monday, 10), Duration.FromMinutes(30));

        doctor.AddScheduleOverride(ScheduleOverride.DayOff(new DateInterval(Monday, Monday)));

        doctor.IsWorkingDuring(time, Moscow).ShouldBeFalse();
    }

    [Fact]
    public void Schedule_can_be_changed()
    {
        var doctor = NewDoctor(Weekdays(Hours(9, 0, 13, 0)));

        doctor.ChangeSchedule(WeeklySchedule.Empty);

        doctor.WorkingHoursOn(Monday).Intervals.ShouldBeEmpty();
    }

    [Fact]
    public void Services_can_be_added_and_removed()
    {
        var service = NewService(30);
        var doctor = NewDoctor(WeeklySchedule.Empty);

        doctor.AddService(service.Id);
        doctor.Provides(service.Id).ShouldBeTrue();
        doctor.Services.ShouldHaveSingleItem();

        doctor.RemoveService(service.Id);
        doctor.Provides(service.Id).ShouldBeFalse();
    }
}

public class ServiceTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void Duration_multiple_of_grid_is_valid(int minutes) =>
        Service.Create(ServiceId.New(), "Приём", Duration.FromMinutes(minutes), Policy).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(0, "service.duration_must_be_positive")]
    [InlineData(-15, "service.duration_must_be_positive")]
    [InlineData(20, "service.duration_not_multiple_of_grid")]
    public void Invalid_duration(int minutes, string code) =>
        Service.Create(ServiceId.New(), "Приём", Duration.FromMinutes(minutes), Policy).Error.Code.ShouldBe(code);

    [Fact]
    public void Name_is_required() =>
        Service.Create(ServiceId.New(), " ", Duration.FromMinutes(15), Policy).Error.Code.ShouldBe("service.name_required");
}
