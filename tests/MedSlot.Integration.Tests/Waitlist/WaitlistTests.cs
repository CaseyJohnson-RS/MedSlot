using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MedSlot.Api.Contracts;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Appointments;
using MedSlot.Application.Waitlist;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Waitlist;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Waitlist;

public class WaitlistTests
{
    private static readonly Instant Start = Msk(NextMonday, 10);

    private readonly DatabaseFixture _fixture;

    public WaitlistTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Result<AppointmentId>> Book(TestClinic clinic, int patient, Instant start, ServiceId? service = null) =>
        _fixture.SendAsync(new BookAppointment(
            clinic.Patients[patient], clinic.Doctor, service ?? clinic.Long30, start, BookingSource.Patient));

    private async Task<WaitlistEntryId> Join(TestClinic clinic, int patient, ServiceId? service = null)
    {
        var id = (await _fixture.SendAsync(new JoinWaitlist(
            clinic.Patients[patient], clinic.Doctor, service ?? clinic.Long30, NextMonday, NextMonday.PlusDays(4)))).Value;

        // Разное время постановки в очередь: порядок FIFO однозначен.
        _fixture.Clock.Advance(Duration.FromMinutes(1));
        return id;
    }

    private async Task<WaitlistEntryDto> Entry(TestClinic clinic, int patient) =>
        (await _fixture.QueryAsync(new GetPatientWaitlist(clinic.Patients[patient]))).Value.ShouldHaveSingleItem();

    /// <summary>Пациент 0 записан на 10:00 и отменяет запись; пациенты из <paramref name="waiting"/> в очереди.</summary>
    private async Task<TestClinic> FreeSlotWithQueue(params int[] waiting)
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 5);
        var booked = (await Book(clinic, 0, Start)).Value;
        foreach (var patient in waiting)
        {
            await Join(clinic, patient);
        }

        (await _fixture.SendAsync(new CancelAppointmentByPatient(booked, clinic.Patients[0]))).IsSuccess.ShouldBeTrue();
        return clinic;
    }

    [Fact]
    public async Task Cancellation_offers_slot_to_first_in_queue_and_holds_it()
    {
        var clinic = await FreeSlotWithQueue(1, 2);

        var first = await Entry(clinic, 1);
        first.Status.ShouldBe(WaitlistEntryStatus.Offered);
        first.Offer.ShouldNotBeNull();
        first.Offer.StartsAt.ShouldBe(Start);
        first.Offer.RespondUntil.ShouldBe(_fixture.Clock.GetCurrentInstant() + _fixture.Policy.WaitlistOfferTtl);
        (await Entry(clinic, 2)).Status.ShouldBe(WaitlistEntryStatus.Active);

        // Пока время удержано, записаться на него нельзя.
        (await Book(clinic, 3, Start)).Error.ShouldBe(AppointmentErrors.SlotAlreadyTaken);
    }

    [Fact]
    public async Task Accepted_offer_becomes_appointment()
    {
        var clinic = await FreeSlotWithQueue(1);
        var offer = (await Entry(clinic, 1)).Offer!.AppointmentId;

        (await _fixture.SendAsync(new AcceptWaitlistOffer(offer, clinic.Patients[1]))).IsSuccess.ShouldBeTrue();

        (await Entry(clinic, 1)).Status.ShouldBe(WaitlistEntryStatus.Fulfilled);
        var appointment = (await _fixture.QueryAsync(new GetAppointment(offer))).Value;
        appointment.Status.ShouldBe(AppointmentStatus.Booked);
        appointment.PatientId.ShouldBe(clinic.Patients[1]);
    }

    [Fact]
    public async Task Declined_offer_goes_to_next_and_decliner_moves_to_back()
    {
        var clinic = await FreeSlotWithQueue(1, 2);
        var offer = (await Entry(clinic, 1)).Offer!.AppointmentId;

        (await _fixture.SendAsync(new DeclineWaitlistOffer(offer, clinic.Patients[1]))).IsSuccess.ShouldBeTrue();

        var decliner = await Entry(clinic, 1);
        decliner.Status.ShouldBe(WaitlistEntryStatus.Active);
        decliner.QueuedAt.ShouldBe(_fixture.Clock.GetCurrentInstant());
        var next = await Entry(clinic, 2);
        next.Status.ShouldBe(WaitlistEntryStatus.Offered);
        next.Offer!.StartsAt.ShouldBe(Start);
    }

    [Fact]
    public async Task Expired_offer_goes_to_next_in_queue()
    {
        var clinic = await FreeSlotWithQueue(1, 2);

        _fixture.Clock.Advance(_fixture.Policy.WaitlistOfferTtl + Duration.FromMinutes(1));
        while ((await _fixture.SendAsync(new ExpireNextWaitlistOffer())).Value)
        {
        }

        (await Entry(clinic, 1)).Status.ShouldBe(WaitlistEntryStatus.Active);
        (await Entry(clinic, 2)).Status.ShouldBe(WaitlistEntryStatus.Offered);
        var expiredCount = await _fixture.WithDbAsync(db => db.WaitlistEntries
            .Where(e => e.PatientId == clinic.Patients[1]).Select(e => e.ExpiredOffersCount).SingleAsync(Ct));
        expiredCount.ShouldBe(1);
    }

    [Fact]
    public async Task Offer_cannot_be_accepted_after_it_expired()
    {
        var clinic = await FreeSlotWithQueue(1);
        var offer = (await Entry(clinic, 1)).Offer!.AppointmentId;

        _fixture.Clock.Advance(_fixture.Policy.WaitlistOfferTtl);

        (await _fixture.SendAsync(new AcceptWaitlistOffer(offer, clinic.Patients[1]))).Error.ShouldBe(AppointmentErrors.OfferExpired);
    }

    [Fact]
    public async Task Concurrent_accept_and_expiry_leave_consistent_state()
    {
        var clinic = await FreeSlotWithQueue(1);
        var offerId = (await Entry(clinic, 1)).Offer!.AppointmentId;
        var expiresAt = _fixture.Clock.GetCurrentInstant() + _fixture.Policy.WaitlistOfferTtl;

        // Пациент принимает за секунду до истечения, а служба истечения уже прочитала то же удержание.
        await using var accepting = _fixture.Factory.Services.CreateAsyncScope();
        await using var expiring = _fixture.Factory.Services.CreateAsyncScope();
        var (acceptAppointment, acceptEntry) = await Load(accepting, offerId);
        var (expireAppointment, expireEntry) = await Load(expiring, offerId);

        acceptAppointment.AcceptOffer(expiresAt - Duration.FromSeconds(1)).IsSuccess.ShouldBeTrue();
        acceptEntry.Fulfill().IsSuccess.ShouldBeTrue();
        expireAppointment.ExpireOffer(expiresAt).IsSuccess.ShouldBeTrue();
        expireEntry.OfferExpired(expiresAt, _fixture.Policy).IsSuccess.ShouldBeTrue();

        (await accepting.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct)).IsSuccess.ShouldBeTrue();
        (await expiring.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct))
            .Error.ShouldBe(ApplicationErrors.ConcurrentModification);

        (await _fixture.QueryAsync(new GetAppointment(offerId))).Value.Status.ShouldBe(AppointmentStatus.Booked);
        (await Entry(clinic, 1)).Status.ShouldBe(WaitlistEntryStatus.Fulfilled);
    }

    [Fact]
    public async Task Clinic_cancellation_makes_no_offer()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var booked = (await Book(clinic, 0, Start)).Value;
        await Join(clinic, 1);

        (await _fixture.SendAsync(new CancelAppointmentByClinic(booked, "doctor_sick"))).IsSuccess.ShouldBeTrue();

        (await Entry(clinic, 1)).Status.ShouldBe(WaitlistEntryStatus.Active);
    }

    [Fact]
    public async Task Too_late_cancellation_makes_no_offer()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var booked = (await Book(clinic, 0, Start)).Value;
        await Join(clinic, 1);

        // За час до приёма: на ответ и дорогу (30 мин + 1 ч) времени уже нет.
        _fixture.Clock.Reset(Start - Duration.FromHours(1));
        (await _fixture.SendAsync(new CancelAppointmentByPatient(booked, clinic.Patients[0]))).IsSuccess.ShouldBeTrue();

        (await Entry(clinic, 1)).Status.ShouldBe(WaitlistEntryStatus.Active);
    }

    [Fact]
    public async Task Busy_patient_and_unfitting_service_are_skipped()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 4);
        var other = await _fixture.CreateClinicAsync();
        var booked = (await Book(clinic, 0, Start, clinic.Short15)).Value;

        await Join(clinic, 1, clinic.Short15); // занят в это время у другого врача
        await Join(clinic, 2, clinic.Long30); // 30 минут не помещаются в освободившиеся 15
        await Join(clinic, 3, clinic.Short15); // подходит
        (await _fixture.SendAsync(new BookAppointment(
            clinic.Patients[1], other.Doctor, other.Short15, Start, BookingSource.Patient))).IsSuccess.ShouldBeTrue();

        (await _fixture.SendAsync(new CancelAppointmentByPatient(booked, clinic.Patients[0]))).IsSuccess.ShouldBeTrue();

        (await Entry(clinic, 1)).Status.ShouldBe(WaitlistEntryStatus.Active);
        (await Entry(clinic, 2)).Status.ShouldBe(WaitlistEntryStatus.Active);
        (await Entry(clinic, 3)).Status.ShouldBe(WaitlistEntryStatus.Offered);
    }

    [Fact]
    public async Task Leaving_waitlist_with_pending_offer_passes_it_on()
    {
        var clinic = await FreeSlotWithQueue(1, 2);
        var entry = await Entry(clinic, 1);

        (await _fixture.SendAsync(new CancelWaitlistEntry(entry.Id, clinic.Patients[1]))).IsSuccess.ShouldBeTrue();

        (await Entry(clinic, 1)).Status.ShouldBe(WaitlistEntryStatus.Cancelled);
        (await _fixture.QueryAsync(new GetAppointment(entry.Offer!.AppointmentId))).Value.Status.ShouldBe(AppointmentStatus.Released);
        (await Entry(clinic, 2)).Status.ShouldBe(WaitlistEntryStatus.Offered);
    }

    [Fact]
    public async Task Join_rules_duplicate_and_limit()
    {
        var first = await _fixture.CreateClinicAsync();
        var second = await _fixture.CreateClinicAsync();
        var patient = first.Patient;

        Task<Result<WaitlistEntryId>> JoinAs(TestClinic clinic, ServiceId service) =>
            _fixture.SendAsync(new JoinWaitlist(patient, clinic.Doctor, service, NextMonday, NextMonday.PlusDays(4)));

        (await JoinAs(first, first.Long30)).IsSuccess.ShouldBeTrue();
        (await JoinAs(first, first.Long30)).Error.ShouldBe(WaitlistErrors.DuplicateEntry);
        (await JoinAs(first, first.Short15)).IsSuccess.ShouldBeTrue();
        (await JoinAs(second, second.Long30)).IsSuccess.ShouldBeTrue();
        (await JoinAs(second, second.Short15)).Error.Code.ShouldBe("waitlist.too_many_entries");
    }

    [Fact]
    public async Task Entries_with_past_dates_expire()
    {
        var clinic = await _fixture.CreateClinicAsync();
        (await _fixture.SendAsync(new JoinWaitlist(clinic.Patient, clinic.Doctor, clinic.Long30, NextMonday, NextMonday))).IsSuccess.ShouldBeTrue();

        _fixture.Clock.Reset(Msk(NextMonday.PlusDays(1), 9));
        (await _fixture.SendAsync(new ExpireStaleWaitlistEntries())).Value.ShouldBeGreaterThanOrEqualTo(1);

        (await Entry(clinic, 0)).Status.ShouldBe(WaitlistEntryStatus.Expired);
    }

    [Fact]
    public async Task Waitlist_flow_over_http()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        using var client = _fixture.Factory.CreateClient();
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var booked = (await Book(clinic, 0, Start)).Value;

        var joined = await client.PostAsJsonAsync("/api/v1/waitlist", new JoinWaitlistRequest(
            clinic.Patients[1].Value, clinic.Doctor.Value, clinic.Long30.Value, NextMonday.ToDateOnly(), NextMonday.PlusDays(4).ToDateOnly()), Ct);
        joined.StatusCode.ShouldBe(HttpStatusCode.Created);

        (await client.PostAsJsonAsync($"/api/v1/appointments/{booked.Value}/cancel", new CancelByPatientRequest(clinic.Patients[0].Value), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var waitlist = await client.GetFromJsonAsync<List<WaitlistEntryResponse>>($"/api/v1/patients/{clinic.Patients[1].Value}/waitlist", json, Ct);
        var offer = waitlist!.ShouldHaveSingleItem().Offer.ShouldNotBeNull();
        offer.StartsAt.ShouldBe(Start.ToDateTimeOffset());

        (await client.PostAsJsonAsync($"/api/v1/waitlist/offers/{offer.AppointmentId}/accept", new WaitlistActionRequest(clinic.Patients[1].Value), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Чужое предложение — как несуществующее.
        (await client.PostAsJsonAsync($"/api/v1/waitlist/offers/{offer.AppointmentId}/decline", new WaitlistActionRequest(clinic.Patients[0].Value), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var appointment = await client.GetFromJsonAsync<AppointmentResponse>($"/api/v1/appointments/{offer.AppointmentId}", json, Ct);
        appointment!.Status.ShouldBe(AppointmentStatus.Booked);
    }

    private static async Task<(Appointment, WaitlistEntry)> Load(AsyncServiceScope scope, AppointmentId offerId)
    {
        var appointment = (await scope.ServiceProvider.GetRequiredService<IAppointmentRepository>().GetByIdAsync(offerId, Ct))!;
        var entry = (await scope.ServiceProvider.GetRequiredService<IWaitlistRepository>().GetByIdAsync(appointment.WaitlistEntryId!.Value, Ct))!;
        return (appointment, entry);
    }
}
