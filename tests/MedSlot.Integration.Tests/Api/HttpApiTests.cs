using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MedSlot.Api.Contracts;
using MedSlot.Domain.Appointments;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Api;

public sealed class HttpApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DatabaseFixture _fixture;
    private readonly HttpClient _client;

    public HttpApiTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
        _client = fixture.Factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    private static BookAppointmentRequest BookRequest(TestClinic clinic, int hour, int minute = 0, int patient = 0) =>
        new(clinic.Patients[patient].Value, clinic.Doctor.Value, clinic.Long30.Value, Msk(NextMonday, hour, minute).ToDateTimeOffset());

    private Task<HttpResponseMessage> Post(string url, object body, string? idempotencyKey = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return (client ?? _client).SendAsync(request, Ct);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        return problem?["code"]?.GetValue<string>();
    }

    private Task<int> AppointmentsOf(TestClinic clinic) =>
        _fixture.WithDbAsync(db => db.Appointments.CountAsync(a => a.DoctorId == clinic.Doctor));

    [Fact]
    public async Task Find_slot_book_and_cancel()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var day = NextMonday.ToDateOnly();

        var slots = await _client.GetFromJsonAsync<List<SlotResponse>>(
            $"/api/v1/doctors/{clinic.Doctor.Value}/slots?serviceId={clinic.Long30.Value}&from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}", Json, Ct);
        slots!.ShouldContain(s => s.StartsAt == Msk(NextMonday, 10).ToDateTimeOffset());

        var booked = await Post("/api/v1/appointments", BookRequest(clinic, 10));
        booked.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await booked.Content.ReadFromJsonAsync<AppointmentCreatedResponse>(Ct))!.Id;
        booked.Headers.Location!.ToString().ShouldEndWith($"/api/v1/appointments/{id}");

        var appointment = await _client.GetFromJsonAsync<AppointmentResponse>(booked.Headers.Location, Json, Ct);
        appointment!.Status.ShouldBe(AppointmentStatus.Booked);
        appointment.StartsAt.ShouldBe(Msk(NextMonday, 10).ToDateTimeOffset());

        var mine = await _client.GetFromJsonAsync<List<AppointmentResponse>>($"/api/v1/patients/{clinic.Patient.Value}/appointments", Json, Ct);
        mine!.ShouldHaveSingleItem().Id.ShouldBe(id);

        (await Post($"/api/v1/appointments/{id}/cancel", new CancelByPatientRequest(clinic.Patient.Value))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetFromJsonAsync<AppointmentResponse>(booked.Headers.Location, Json, Ct))!.Status.ShouldBe(AppointmentStatus.Cancelled);
    }

    [Fact]
    public async Task Doctor_schedule_is_returned_in_clinic_time()
    {
        var clinic = await _fixture.CreateClinicAsync();

        var schedule = await _client.GetFromJsonAsync<DoctorScheduleResponse>($"/api/v1/doctors/{clinic.Doctor.Value}/schedule", Json, Ct);

        schedule!.TimeZone.ShouldBe("Europe/Moscow");
        schedule.Weekly.Select(d => d.Day).ShouldBe([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
        schedule.Weekly[0].Hours[0].ShouldBe(new WorkingIntervalResponse(new TimeOnly(9, 0), new TimeOnly(13, 0)));
    }

    [Fact]
    public async Task Invalid_request_returns_validation_problem()
    {
        var response = await Post("/api/v1/appointments", new BookAppointmentRequest(Guid.Empty, Guid.Empty, Guid.Empty, default));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        problem!.Errors.Keys.ShouldBe(["PatientId", "DoctorId", "ServiceId", "StartsAt"], ignoreOrder: true);
    }

    [Fact]
    public async Task Slots_range_is_validated()
    {
        var clinic = await _fixture.CreateClinicAsync();

        var response = await _client.GetAsync(
            $"/api/v1/doctors/{clinic.Doctor.Value}/slots?serviceId={clinic.Long30.Value}&from=2026-11-10&to=2026-11-09", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Domain_errors_map_to_http_status_and_code()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);

        var unknownDoctor = await Post("/api/v1/appointments", BookRequest(clinic, 10) with { DoctorId = Guid.NewGuid() });
        unknownDoctor.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCode(unknownDoctor)).ShouldBe("doctor.not_found");

        var offGrid = await Post("/api/v1/appointments", BookRequest(clinic, 10, 7));
        offGrid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCode(offGrid)).ShouldBe("appointment.slot_not_in_schedule");

        (await Post("/api/v1/appointments", BookRequest(clinic, 11))).StatusCode.ShouldBe(HttpStatusCode.Created);
        var taken = await Post("/api/v1/appointments", BookRequest(clinic, 11, patient: 1));
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCode(taken)).ShouldBe("appointment.slot_already_taken");
    }

    [Fact]
    public async Task Repeated_request_with_same_key_returns_saved_response()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var key = Guid.NewGuid().ToString();

        var first = await Post("/api/v1/appointments", BookRequest(clinic, 12), key);
        var second = await Post("/api/v1/appointments", BookRequest(clinic, 12), key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        second.Headers.Location.ShouldBe(first.Headers.Location);
        (await second.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        (await AppointmentsOf(clinic)).ShouldBe(1);
    }

    [Fact]
    public async Task Same_key_with_different_body_is_rejected()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var key = Guid.NewGuid().ToString();
        (await Post("/api/v1/appointments", BookRequest(clinic, 14), key)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var other = await Post("/api/v1/appointments", BookRequest(clinic, 15), key);

        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCode(other)).ShouldBe("idempotency.key_reused");
        (await AppointmentsOf(clinic)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_requests_with_same_key_create_one_appointment()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var key = Guid.NewGuid().ToString();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                await go.Task;
                return await Post("/api/v1/appointments", BookRequest(clinic, 16), key);
            }))
            .ToList();
        go.SetResult();
        var responses = await Task.WhenAll(tasks);

        // Победитель — 201; остальные либо получили сохранённый ответ (тот же 201), либо 409 «ещё выполняется».
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        created.ShouldNotBeEmpty();
        created.Select(r => r.Headers.Location).Distinct().ShouldHaveSingleItem();
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            (await ErrorCode(conflict)).ShouldBe("idempotency.request_in_progress");
        }

        (await AppointmentsOf(clinic)).ShouldBe(1);
    }

    [Fact]
    public async Task Failed_request_is_not_saved_and_is_executed_again()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var key = Guid.NewGuid().ToString();

        var first = await Post("/api/v1/appointments", BookRequest(clinic, 10, 7), key);
        var second = await Post("/api/v1/appointments", BookRequest(clinic, 10, 7), key);

        first.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        second.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
    }

    [Fact]
    public async Task Key_expires_after_24_hours()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var key = Guid.NewGuid().ToString();
        (await Post("/api/v1/appointments", BookRequest(clinic, 9), key)).StatusCode.ShouldBe(HttpStatusCode.Created);

        _fixture.Clock.Reset(BaseNow + Duration.FromHours(25));
        var reused = await Post("/api/v1/appointments", BookRequest(clinic, 9, 30, patient: 1), key);

        reused.StatusCode.ShouldBe(HttpStatusCode.Created);
        reused.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
    }

    [Fact]
    public async Task Invalid_key_is_rejected()
    {
        var clinic = await _fixture.CreateClinicAsync();

        var response = await Post("/api/v1/appointments", BookRequest(clinic, 10), new string('k', 101));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCode(response)).ShouldBe("idempotency.invalid_key");
    }

    [Fact]
    public async Task Booking_is_rate_limited()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 3);
        await using var limited = _fixture.Factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:BookingPermitLimit", "2"));
        using var client = limited.CreateClient();

        (await Post("/api/v1/appointments", BookRequest(clinic, 10, patient: 0), client: client)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Post("/api/v1/appointments", BookRequest(clinic, 11, patient: 1), client: client)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var third = await Post("/api/v1/appointments", BookRequest(clinic, 12, patient: 2), client: client);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.ShouldNotBeNull();
        (await ErrorCode(third)).ShouldBe("rate_limit.exceeded");

        // Чтение не ограничено.
        (await client.GetAsync($"/api/v1/patients/{clinic.Patient.Value}/appointments", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OpenApi_documents_idempotency_header_and_errors()
    {
        var document = await _client.GetFromJsonAsync<JsonObject>("/openapi/v1.json", Ct);
        var book = document!["paths"]!["/api/v1/appointments"]!["post"]!;

        book["parameters"]!.AsArray().ShouldContain(p => p!["name"]!.GetValue<string>() == "Idempotency-Key" && p["in"]!.GetValue<string>() == "header");
        book["responses"]!.AsObject().Select(r => r.Key).ShouldBe(["201", "400", "403", "404", "409", "422", "429"], ignoreOrder: true);

        var status = document["components"]!["schemas"]!["AppointmentStatus"];
        status.ShouldNotBeNull();
    }
}
