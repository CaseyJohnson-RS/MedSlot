using MedSlot.Application.Abstractions;
using MedSlot.Application.Scheduling;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MedSlot.Integration.Tests.Persistence;

public class SchemaTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task All_migrations_are_applied()
    {
        var pending = await fixture.WithDbAsync(db => db.Database.GetPendingMigrationsAsync());

        pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task Exclusion_constraints_exist()
    {
        var names = await fixture.WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT conname AS \"Value\" FROM pg_constraint WHERE contype = 'x' ORDER BY conname")
            .ToListAsync());

        names.ShouldBe(["ex_appointments_doctor_no_overlap", "ex_appointments_patient_no_overlap"]);
    }

    [Fact]
    public async Task Doctor_schedule_and_overrides_round_trip_through_jsonb()
    {
        var clinic = await fixture.CreateClinicAsync();
        var vacation = new DateInterval(DatabaseFixture.NextMonday, DatabaseFixture.NextMonday.PlusDays(4));
        var custom = new DateInterval(DatabaseFixture.NextMonday.PlusDays(7), DatabaseFixture.NextMonday.PlusDays(7));

        // Изменение загруженного агрегата: проверяет отслеживание изменений приватной коллекции.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var doctor = (await scope.ServiceProvider.GetRequiredService<IDoctorRepository>().GetByIdAsync(clinic.Doctor, TestContext.Current.CancellationToken))!;
            doctor.AddScheduleOverride(ScheduleOverride.DayOff(vacation, "Отпуск")).IsSuccess.ShouldBeTrue();
            doctor.AddScheduleOverride(ScheduleOverride.CustomHours(
                custom, [new LocalTimeRange(new LocalTime(15, 0), new LocalTime(17, 0))]).Value).IsSuccess.ShouldBeTrue();
            (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        }

        var schedule = (await fixture.QueryAsync(new GetDoctorSchedule(clinic.Doctor))).Value;

        schedule.Weekly.Keys.ShouldBe([IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday]);
        schedule.Weekly[IsoDayOfWeek.Monday].ShouldBe(
        [
            new WorkingIntervalDto(new LocalTime(9, 0), new LocalTime(13, 0)),
            new WorkingIntervalDto(new LocalTime(14, 0), new LocalTime(18, 0)),
        ]);
        schedule.Overrides.Count.ShouldBe(2);
        schedule.Overrides[0].ShouldBe(new ScheduleOverrideDto(vacation.Start, vacation.End, ScheduleOverrideKind.DayOff, [], "Отпуск"), new OverrideComparer());
        schedule.Overrides[1].Kind.ShouldBe(ScheduleOverrideKind.CustomHours);
        schedule.Overrides[1].Hours.ShouldBe([new WorkingIntervalDto(new LocalTime(15, 0), new LocalTime(17, 0))]);
    }

    [Fact]
    public async Task Unknown_doctor_is_not_found()
    {
        var result = await fixture.QueryAsync(new GetDoctorSchedule(DoctorId.New()));

        result.Error.Code.ShouldBe("doctor.not_found");
    }

    // Списки внутри record сравниваются по ссылке, поэтому сравниваем поля явно.
    private sealed class OverrideComparer : IEqualityComparer<ScheduleOverrideDto>
    {
        public bool Equals(ScheduleOverrideDto? x, ScheduleOverrideDto? y) =>
            x!.From == y!.From && x.To == y.To && x.Kind == y.Kind && x.Reason == y.Reason && x.Hours.SequenceEqual(y.Hours);

        public int GetHashCode(ScheduleOverrideDto obj) => obj.From.GetHashCode();
    }
}
