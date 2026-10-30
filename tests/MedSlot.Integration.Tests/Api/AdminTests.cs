using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MedSlot.Api.Contracts;
using MedSlot.Application.Appointments;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Identity;
using MedSlot.Integration.Tests.Infrastructure;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Api;

public sealed class AdminTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DatabaseFixture _fixture;
    private readonly HttpClient _client;

    public AdminTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
        _client = fixture.Factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    private async Task<HttpResponseMessage> AsAdmin(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _fixture.AdminTokenAsync());
        return await _client.SendAsync(request, Ct);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();

    [Fact]
    public async Task Admin_endpoints_are_for_admins_only()
    {
        var clinic = await _fixture.CreateClinicAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/patients/{clinic.Patient.Value}/lift-ban");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _fixture.DoctorTokenAsync(clinic.Doctor));

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_books_on_behalf_of_patient()
    {
        var clinic = await _fixture.CreateClinicAsync();

        var response = await AsAdmin(HttpMethod.Post, "/api/v1/admin/appointments", new AdminBookAppointmentRequest(
            clinic.Patient.Value, clinic.Doctor.Value, clinic.Long30.Value, Msk(NextMonday, 10).ToDateTimeOffset()));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<AppointmentCreatedResponse>(Ct))!.Id;
        (await _fixture.WithDbAsync(db => Task.FromResult(db.Appointments.Single(a => a.Id == new AppointmentId(id)).BookedBy)))
            .ShouldBe(BookingSource.Admin);
    }

    [Fact]
    public async Task Created_doctor_account_can_log_in()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var email = $"doctor-{Guid.NewGuid():N}@medslot.local";

        var created = await AsAdmin(HttpMethod.Post, "/api/v1/admin/staff",
            new CreateStaffRequest(email, "Doctor-Password-1", UserRole.Doctor, clinic.Doctor.Value));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var duplicate = await AsAdmin(HttpMethod.Post, "/api/v1/admin/staff",
            new CreateStaffRequest($"x-{email}", "Doctor-Password-1", UserRole.Doctor, clinic.Doctor.Value));
        (await ErrorCode(duplicate)).ShouldBe("auth.doctor_has_account");

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Doctor-Password-1"), Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<TokensResponse>(Json, Ct))!.Role.ShouldBe(UserRole.Doctor);
    }

    [Fact]
    public async Task Day_off_over_booked_appointments_requires_cancelling_them_first()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var id = (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday, 10), BookingSource.Patient))).Value;
        var vacation = new ScheduleOverrideRequest(NextMonday.ToDateOnly(), NextMonday.PlusDays(4).ToDateOnly(), ScheduleOverrideKind.DayOff, null, "Отпуск");
        var url = $"/api/v1/admin/doctors/{clinic.Doctor.Value}/schedule-overrides";

        var conflict = await AsAdmin(HttpMethod.Post, url, vacation);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCode(conflict)).ShouldBe("doctor.schedule_conflicts_with_appointments");

        (await _fixture.SendAsync(new CancelAppointmentByClinic(id, "doctor_vacation"))).IsSuccess.ShouldBeTrue();
        (await AsAdmin(HttpMethod.Post, url, vacation)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var slots = await _client.GetFromJsonAsync<List<SlotResponse>>(
            $"/api/v1/doctors/{clinic.Doctor.Value}/slots?serviceId={clinic.Long30.Value}&from={NextMonday.ToDateOnly():yyyy-MM-dd}&to={NextMonday.ToDateOnly():yyyy-MM-dd}",
            Json, Ct);
        slots.ShouldNotBeNull().ShouldBeEmpty();

        var from = NextMonday.ToDateOnly().ToString("yyyy-MM-dd", null);
        var to = NextMonday.PlusDays(4).ToDateOnly().ToString("yyyy-MM-dd", null);
        (await AsAdmin(HttpMethod.Delete, $"{url}?from={from}&to={to}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AsAdmin(HttpMethod.Delete, $"{url}?from={from}&to={to}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Custom_hours_keep_appointments_inside_them()
    {
        var clinic = await _fixture.CreateClinicAsync();
        (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday, 15), BookingSource.Patient))).IsSuccess.ShouldBeTrue();
        var url = $"/api/v1/admin/doctors/{clinic.Doctor.Value}/schedule-overrides";

        // 14:00–16:00 вмещает запись на 15:00 — можно; 09:00–12:00 — нет.
        (await AsAdmin(HttpMethod.Post, url, new ScheduleOverrideRequest(
            NextMonday.ToDateOnly(), NextMonday.ToDateOnly(), ScheduleOverrideKind.CustomHours,
            [new WorkingIntervalRequest(new TimeOnly(14, 0), new TimeOnly(16, 0))], null))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var next = NextMonday.PlusDays(7);
        (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(next, 15), BookingSource.Admin))).IsSuccess.ShouldBeTrue();
        (await ErrorCode(await AsAdmin(HttpMethod.Post, url, new ScheduleOverrideRequest(
            next.ToDateOnly(), next.ToDateOnly(), ScheduleOverrideKind.CustomHours,
            [new WorkingIntervalRequest(new TimeOnly(9, 0), new TimeOnly(12, 0))], null))))
            .ShouldBe("doctor.schedule_conflicts_with_appointments");
    }

    [Fact]
    public async Task Lifting_ban_restores_online_booking()
    {
        var clinic = await _fixture.CreateClinicAsync();
        foreach (var hour in new[] { 10, 11, 12 })
        {
            var id = (await _fixture.SendAsync(new BookAppointment(
                clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday, hour), BookingSource.Patient))).Value;
            _fixture.Clock.Reset(Msk(NextMonday, 9));
            await _fixture.SendAsync(new CancelAppointmentByPatient(id, clinic.Patient));
            _fixture.Clock.Reset(BaseNow);
        }

        _fixture.Clock.Reset(Msk(NextMonday, 9));
        var booking = new BookAppointment(clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday.PlusDays(1), 10), BookingSource.Patient);
        (await _fixture.SendAsync(booking)).Error.Code.ShouldBe("patient.booking_banned");

        (await AsAdmin(HttpMethod.Post, $"/api/v1/admin/patients/{clinic.Patient.Value}/lift-ban")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _fixture.SendAsync(booking)).IsSuccess.ShouldBeTrue();
    }

    private static WeeklyScheduleRequest Weekdays(params DayOfWeek[] days) => new(
        days.Select(d => new WorkingDayRequest(d, [new WorkingIntervalRequest(new TimeOnly(10, 0), new TimeOnly(16, 0))])).ToList());

    [Fact]
    public async Task Weekly_schedule_is_replaced_entirely()
    {
        var clinic = await _fixture.CreateClinicAsync();

        (await AsAdmin(HttpMethod.Put, $"/api/v1/admin/doctors/{clinic.Doctor.Value}/schedule",
            Weekdays(DayOfWeek.Monday, DayOfWeek.Wednesday))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var schedule = await _client.GetFromJsonAsync<DoctorScheduleResponse>($"/api/v1/doctors/{clinic.Doctor.Value}/schedule", Json, Ct);
        schedule.ShouldNotBeNull().Weekly.Select(d => d.Day).ShouldBe([DayOfWeek.Monday, DayOfWeek.Wednesday], ignoreOrder: true);
        schedule.Weekly.ShouldAllBe(d => d.Hours.Single() == new WorkingIntervalResponse(new TimeOnly(10, 0), new TimeOnly(16, 0)));
    }

    [Fact]
    public async Task Weekly_schedule_change_over_upcoming_appointments_requires_cancelling_them_first()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var thursday = NextMonday.PlusDays(3);
        var id = (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(thursday, 10), BookingSource.Patient))).Value;
        var url = $"/api/v1/admin/doctors/{clinic.Doctor.Value}/schedule";
        var withoutThursday = Weekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Friday);

        var conflict = await AsAdmin(HttpMethod.Put, url, withoutThursday);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCode(conflict)).ShouldBe("doctor.schedule_conflicts_with_appointments");

        // Запись на 10:00 укладывается в новые часы четверга 10:00–16:00.
        (await AsAdmin(HttpMethod.Put, url, Weekdays(DayOfWeek.Thursday))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _fixture.SendAsync(new CancelAppointmentByClinic(id, "schedule_change"))).IsSuccess.ShouldBeTrue();
        (await AsAdmin(HttpMethod.Put, url, withoutThursday)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Past_appointments_do_not_block_weekly_schedule_change()
    {
        var clinic = await _fixture.CreateClinicAsync();
        (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday, 10), BookingSource.Patient))).IsSuccess.ShouldBeTrue();

        _fixture.Clock.Reset(Msk(NextMonday, 12));
        (await AsAdmin(HttpMethod.Put, $"/api/v1/admin/doctors/{clinic.Doctor.Value}/schedule", Weekdays(DayOfWeek.Friday)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Invalid_weekly_schedule_is_rejected()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var url = $"/api/v1/admin/doctors/{clinic.Doctor.Value}/schedule";

        var duplicateDay = await AsAdmin(HttpMethod.Put, url, Weekdays(DayOfWeek.Monday, DayOfWeek.Monday));
        duplicateDay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var overlapping = await AsAdmin(HttpMethod.Put, url, new WeeklyScheduleRequest(
        [
            new WorkingDayRequest(DayOfWeek.Monday,
            [
                new WorkingIntervalRequest(new TimeOnly(9, 0), new TimeOnly(13, 0)),
                new WorkingIntervalRequest(new TimeOnly(12, 0), new TimeOnly(18, 0)),
            ]),
        ]));
        (await ErrorCode(overlapping)).ShouldBe("doctor.overlapping_working_hours");
    }
}
