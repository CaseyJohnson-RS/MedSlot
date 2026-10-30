using MedSlot.Application.Abstractions;
using MedSlot.Application.Appointments;
using MedSlot.Application.Scheduling;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Appointments;

public class BookingTests
{
    private readonly DatabaseFixture _fixture;

    public BookingTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
    }

    private Task<Result<AppointmentId>> Book(TestClinic clinic, PatientId patient, ServiceId service, Instant start, DoctorId? doctor = null) =>
        _fixture.SendAsync(new BookAppointment(patient, doctor ?? clinic.Doctor, service, start, BookingSource.Patient));

    [Fact]
    public async Task Concurrent_bookings_of_one_slot_create_exactly_one_appointment()
    {
        const int attempts = 50;
        var clinic = await _fixture.CreateClinicAsync(patients: attempts);
        var start = Msk(NextMonday, 10);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Все запросы стартуют одновременно, каждый в своём scope и со своим подключением.
        var tasks = clinic.Patients
            .Select(patient => Task.Run(async () =>
            {
                await go.Task;
                return await Book(clinic, patient, clinic.Long30, start);
            }))
            .ToList();
        go.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => r.IsFailure).ShouldAllBe(r => r.Error == AppointmentErrors.SlotAlreadyTaken);

        var stored = await _fixture.WithDbAsync(db => db.Appointments.CountAsync(a => a.DoctorId == clinic.Doctor));
        stored.ShouldBe(1);
    }

    [Fact]
    public async Task Overlapping_appointments_of_different_length_are_rejected()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 3);

        (await Book(clinic, clinic.Patients[0], clinic.Long30, Msk(NextMonday, 10))).IsSuccess.ShouldBeTrue();

        // 10:15–10:30 внутри 10:00–10:30.
        (await Book(clinic, clinic.Patients[1], clinic.Short15, Msk(NextMonday, 10, 15)))
            .Error.ShouldBe(AppointmentErrors.SlotAlreadyTaken);

        // 10:30 — встык, полуоткрытые интервалы не пересекаются.
        (await Book(clinic, clinic.Patients[2], clinic.Short15, Msk(NextMonday, 10, 30))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Patient_cannot_be_at_two_doctors_at_once()
    {
        var first = await _fixture.CreateClinicAsync();
        var second = await _fixture.CreateClinicAsync();
        var start = Msk(NextMonday, 11);

        (await Book(first, first.Patient, first.Long30, start)).IsSuccess.ShouldBeTrue();

        (await Book(second, first.Patient, second.Short15, start + Duration.FromMinutes(15)))
            .Error.ShouldBe(AppointmentErrors.PatientTimeConflict);
    }

    [Fact]
    public async Task Cancelled_appointment_frees_the_slot()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var start = Msk(NextMonday, 12);
        var id = (await Book(clinic, clinic.Patients[0], clinic.Long30, start)).Value;

        (await _fixture.SendAsync(new CancelAppointmentByPatient(id, clinic.Patients[0]))).IsSuccess.ShouldBeTrue();

        (await Book(clinic, clinic.Patients[1], clinic.Long30, start)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Booking_outside_schedule_grid_is_rejected()
    {
        var clinic = await _fixture.CreateClinicAsync();

        (await Book(clinic, clinic.Patient, clinic.Long30, Msk(NextMonday, 10, 7))).Error.ShouldBe(AppointmentErrors.SlotNotInSchedule);
        (await Book(clinic, clinic.Patient, clinic.Long30, Msk(NextMonday, 13, 0))).Error.ShouldBe(AppointmentErrors.SlotNotInSchedule);
    }

    [Fact]
    public async Task Unknown_references_are_not_found()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var start = Msk(NextMonday, 10);

        (await Book(clinic, clinic.Patient, clinic.Long30, start, DoctorId.New())).Error.Code.ShouldBe("doctor.not_found");
        (await Book(clinic, clinic.Patient, ServiceId.New(), start)).Error.Code.ShouldBe("service.not_found");
        (await Book(clinic, PatientId.New(), clinic.Long30, start)).Error.Code.ShouldBe("patient.not_found");
    }

    [Fact]
    public async Task Three_late_cancellations_ban_online_booking()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var ids = new List<AppointmentId>();
        foreach (var hour in new[] { 10, 11, 12 })
        {
            ids.Add((await Book(clinic, clinic.Patient, clinic.Long30, Msk(NextMonday, hour))).Value);
        }

        // Меньше чем за 24 часа до всех трёх приёмов.
        _fixture.Clock.Reset(Msk(NextMonday, 9));
        foreach (var id in ids)
        {
            (await _fixture.SendAsync(new CancelAppointmentByPatient(id, clinic.Patient))).IsSuccess.ShouldBeTrue();
        }

        (await Book(clinic, clinic.Patient, clinic.Long30, Msk(NextMonday.PlusDays(1), 10)))
            .Error.Code.ShouldBe("patient.booking_banned");

        // Администратор записать может.
        (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday.PlusDays(1), 10), BookingSource.Admin)))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_changes_of_one_appointment_are_detected()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var id = (await Book(clinic, clinic.Patient, clinic.Long30, Msk(NextMonday, 15))).Value;

        await using var first = _fixture.Factory.Services.CreateAsyncScope();
        await using var second = _fixture.Factory.Services.CreateAsyncScope();
        var a1 = (await first.ServiceProvider.GetRequiredService<IAppointmentRepository>().GetByIdAsync(id, TestContext.Current.CancellationToken))!;
        var a2 = (await second.ServiceProvider.GetRequiredService<IAppointmentRepository>().GetByIdAsync(id, TestContext.Current.CancellationToken))!;

        // Оба прочитали запись в статусе Booked и меняют её по-разному.
        a1.CancelByPatient(BaseNow, _fixture.Policy).IsSuccess.ShouldBeTrue();
        a2.CancelByClinic(BaseNow, "doctor_sick").IsSuccess.ShouldBeTrue();

        (await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken))
            .Error.ShouldBe(ApplicationErrors.ConcurrentModification);

        var stored = await _fixture.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
        stored.CancelledBy.ShouldBe(CancellationInitiator.Patient);
    }

    [Fact]
    public async Task Visit_lifecycle_through_handlers()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var start = Msk(NextMonday, 16);
        var completed = (await Book(clinic, clinic.Patient, clinic.Long30, start)).Value;
        var noShow = (await Book(clinic, clinic.Patient, clinic.Long30, start + Duration.FromHours(1))).Value;

        (await _fixture.SendAsync(new CompleteAppointment(completed))).Error.ShouldBe(AppointmentErrors.NotStartedYet);

        _fixture.Clock.Reset(start + Duration.FromHours(1) + Duration.FromMinutes(5));
        (await _fixture.SendAsync(new CompleteAppointment(completed))).IsSuccess.ShouldBeTrue();
        (await _fixture.SendAsync(new MarkAppointmentNoShow(noShow))).IsSuccess.ShouldBeTrue();
        (await _fixture.SendAsync(new CancelAppointmentByClinic(completed, "doctor_sick"))).Error.Code.ShouldBe("appointment.invalid_status");

        var list = (await _fixture.QueryAsync(new GetPatientAppointments(clinic.Patient))).Value;
        list.Select(a => a.Status).ShouldBe([AppointmentStatus.Completed, AppointmentStatus.NoShow]);
    }

    [Fact]
    public async Task Patient_cannot_cancel_someone_elses_appointment()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var id = (await Book(clinic, clinic.Patients[0], clinic.Long30, Msk(NextMonday, 17))).Value;

        (await _fixture.SendAsync(new CancelAppointmentByPatient(id, clinic.Patients[1]))).Error.Code.ShouldBe("appointment.not_found");
    }

    [Fact]
    public async Task Available_slots_exclude_booked_time()
    {
        var clinic = await _fixture.CreateClinicAsync();
        (await Book(clinic, clinic.Patient, clinic.Long30, Msk(NextMonday, 10))).IsSuccess.ShouldBeTrue();

        var slots = (await _fixture.QueryAsync(new GetAvailableSlots(clinic.Doctor, clinic.Long30, NextMonday, NextMonday))).Value;
        var starts = slots.Select(s => s.StartsAt).ToList();

        starts.ShouldContain(Msk(NextMonday, 9, 30));
        starts.ShouldNotContain(Msk(NextMonday, 9, 45));
        starts.ShouldNotContain(Msk(NextMonday, 10, 0));
        starts.ShouldNotContain(Msk(NextMonday, 10, 15));
        starts.ShouldContain(Msk(NextMonday, 10, 30));
    }

    [Fact]
    public async Task Slots_query_validates_date_range()
    {
        var clinic = await _fixture.CreateClinicAsync();

        (await _fixture.QueryAsync(new GetAvailableSlots(clinic.Doctor, clinic.Long30, NextMonday, NextMonday.PlusDays(-1))))
            .Error.Code.ShouldBe("common.invalid_date_range");
        (await _fixture.QueryAsync(new GetAvailableSlots(clinic.Doctor, clinic.Long30, NextMonday, NextMonday.PlusDays(GetAvailableSlots.MaxDays))))
            .Error.Code.ShouldBe("common.invalid_date_range");
    }
}
