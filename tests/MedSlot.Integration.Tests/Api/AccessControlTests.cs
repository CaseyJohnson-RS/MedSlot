using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MedSlot.Api.Contracts;
using MedSlot.Application.Appointments;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Integration.Tests.Infrastructure;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Api;

public sealed class AccessControlTests : IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly HttpClient _client;

    public AccessControlTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
        _client = fixture.Factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, Ct);
    }

    /// <summary>Запись пациента 0 к врачу клиники на 10:00 следующего понедельника.</summary>
    private async Task<AppointmentId> Booked(TestClinic clinic) =>
        (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday, 10), BookingSource.Patient))).Value;

    [Fact]
    public async Task Booking_requires_patient_token()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var request = new BookAppointmentRequest(clinic.Doctor.Value, clinic.Long30.Value, Msk(NextMonday, 10).ToDateTimeOffset());

        (await Send(HttpMethod.Post, "/api/v1/appointments", null, request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, "/api/v1/appointments", await _fixture.DoctorTokenAsync(clinic.Doctor), request))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Schedule_and_slots_are_public()
    {
        var clinic = await _fixture.CreateClinicAsync();

        (await Send(HttpMethod.Get, $"/api/v1/doctors/{clinic.Doctor.Value}/schedule", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patient_cannot_see_or_cancel_someone_elses_appointment()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var id = await Booked(clinic);
        var stranger = await _fixture.AccessTokenAsync(clinic.Patients[1]);

        (await Send(HttpMethod.Get, $"/api/v1/appointments/{id.Value}", stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/appointments/{id.Value}/cancel", stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/v1/patients/{clinic.Patient.Value}/appointments", stranger)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await _fixture.QueryAsync(new GetAppointment(id))).Value.Status.ShouldBe(AppointmentStatus.Booked);
    }

    [Fact]
    public async Task Doctor_sees_and_marks_only_own_appointments()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var other = await _fixture.CreateClinicAsync();
        var id = await Booked(clinic);
        var own = await _fixture.DoctorTokenAsync(clinic.Doctor);
        var foreign = await _fixture.DoctorTokenAsync(other.Doctor);

        (await Send(HttpMethod.Get, $"/api/v1/appointments/{id.Value}", own)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, $"/api/v1/appointments/{id.Value}", foreign)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        _fixture.Clock.Reset(Msk(NextMonday, 10, 5));
        own = await _fixture.DoctorTokenAsync(clinic.Doctor);
        foreign = await _fixture.DoctorTokenAsync(other.Doctor);

        (await Send(HttpMethod.Post, $"/api/v1/appointments/{id.Value}/complete", foreign)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"/api/v1/appointments/{id.Value}/complete", await _fixture.AccessTokenAsync(clinic.Patient)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"/api/v1/appointments/{id.Value}/complete", own)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Admin_can_see_any_patient_and_appointment()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var id = await Booked(clinic);
        var admin = await _fixture.AdminTokenAsync();

        (await Send(HttpMethod.Get, $"/api/v1/appointments/{id.Value}", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = await Send(HttpMethod.Get, $"/api/v1/patients/{clinic.Patient.Value}/appointments", admin);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        (await list.Content.ReadFromJsonAsync<List<AppointmentResponse>>(json, Ct))!.ShouldHaveSingleItem().Id.ShouldBe(id.Value);
    }

    [Fact]
    public async Task Expired_access_token_is_rejected()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var token = await _fixture.AccessTokenAsync(clinic.Patient);

        _fixture.Clock.Advance(Duration.FromMinutes(16));

        (await Send(HttpMethod.Get, "/api/v1/me/appointments", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
