using MedSlot.Domain.Common;
using static MedSlot.Domain.Tests.TestData;

namespace MedSlot.Domain.Tests.Common;

public class TimeRangeTests
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 11, 2, 9, 0);

    [Fact]
    public void End_must_be_after_start()
    {
        Should.Throw<ArgumentException>(() => new TimeRange(T0, T0));
        Should.Throw<ArgumentException>(() => new TimeRange(T0, T0 - Duration.FromMinutes(1)));
    }

    [Theory]
    [InlineData(0, 30, 30, 60, false)] // встык — не пересекаются
    [InlineData(0, 30, 29, 60, true)]
    [InlineData(0, 60, 15, 30, true)] // вложенный
    [InlineData(30, 60, 0, 30, false)]
    public void Overlaps_uses_half_open_intervals(int aStart, int aEnd, int bStart, int bEnd, bool expected)
    {
        var a = new TimeRange(T0 + Duration.FromMinutes(aStart), T0 + Duration.FromMinutes(aEnd));
        var b = new TimeRange(T0 + Duration.FromMinutes(bStart), T0 + Duration.FromMinutes(bEnd));

        a.Overlaps(b).ShouldBe(expected);
        b.Overlaps(a).ShouldBe(expected);
    }

    [Fact]
    public void Contains_and_duration()
    {
        var outer = TimeRange.FromStart(T0, Duration.FromHours(1));
        var inner = TimeRange.FromStart(T0 + Duration.FromMinutes(15), Duration.FromMinutes(30));

        outer.Duration.ShouldBe(Duration.FromHours(1));
        outer.ToString().ShouldBe("[2026-11-02T09:00:00Z, 2026-11-02T10:00:00Z)");
        outer.Contains(inner).ShouldBeTrue();
        inner.Contains(outer).ShouldBeFalse();
    }
}

public class LocalTimeRangeTests
{
    [Fact]
    public void End_must_be_after_start() =>
        Should.Throw<ArgumentException>(() => Hours(10, 0, 9, 0));

    [Fact]
    public void Converts_to_instants_in_clinic_zone()
    {
        var range = Hours(9, 0, 18, 0).ToTimeRange(Monday, Moscow)!.Value;

        range.Start.ShouldBe(Instant.FromUtc(2026, 11, 2, 6, 0));
        range.End.ShouldBe(Instant.FromUtc(2026, 11, 2, 15, 0));
    }

    [Fact]
    public void Day_with_spring_forward_is_one_hour_shorter()
    {
        var range = Hours(1, 0, 5, 0).ToTimeRange(new LocalDate(2026, 3, 29), Berlin)!.Value;

        range.Duration.ShouldBe(Duration.FromHours(3));
    }

    [Fact]
    public void Day_with_fall_back_is_one_hour_longer()
    {
        var range = Hours(1, 0, 5, 0).ToTimeRange(new LocalDate(2026, 10, 25), Berlin)!.Value;

        range.Duration.ShouldBe(Duration.FromHours(5));
    }

    [Fact]
    public void Interval_inside_skipped_hour_does_not_exist() =>
        Hours(2, 30, 3, 0).ToTimeRange(new LocalDate(2026, 3, 29), Berlin).ShouldBeNull();
}

public class ResultTests
{
    private static readonly DomainError SomeError = new("test.error", "Ошибка");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBe(DomainError.None);
    }

    [Fact]
    public void Failure_from_error()
    {
        Result result = SomeError;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SomeError);
    }

    [Fact]
    public void Failure_requires_real_error()
    {
        Should.Throw<ArgumentException>(() => Result.Failure(DomainError.None));
        Should.Throw<ArgumentException>(() =>
        {
            Result<int> _ = DomainError.None;
        });
    }

    [Fact]
    public void Value_of_failed_result_throws()
    {
        Result<int> result = SomeError;

        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Value_of_successful_result()
    {
        Result<int> result = 42;

        result.Value.ShouldBe(42);
        Result.Success<IReadOnlyList<int>>([1, 2]).Value.Count.ShouldBe(2);
    }
}

public class EntityTests
{
    [Fact]
    public void Entities_are_equal_by_type_and_id()
    {
        var id = ServiceId.New();
        var a = Domain.Services.Service.Create(id, "А", Duration.FromMinutes(15), Policy).Value;
        var b = Domain.Services.Service.Create(id, "Б", Duration.FromMinutes(30), Policy).Value;
        var other = Domain.Services.Service.Create(ServiceId.New(), "А", Duration.FromMinutes(15), Policy).Value;

        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.Equals(other).ShouldBeFalse();
        a.Equals(null).ShouldBeFalse();
    }

    [Fact]
    public void Ids_are_unique_and_printable()
    {
        var id = DoctorId.New();

        id.ShouldNotBe(DoctorId.New());
        id.ToString().ShouldBe(id.Value.ToString());
        PatientId.New().ToString().ShouldNotBeEmpty();
        AppointmentId.New().ToString().ShouldNotBeEmpty();
        WaitlistEntryId.New().ToString().ShouldNotBeEmpty();
        ServiceId.New().ToString().ShouldNotBeEmpty();
    }
}
