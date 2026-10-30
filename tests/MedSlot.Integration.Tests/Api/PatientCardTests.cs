using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MedSlot.Api.Contracts;
using MedSlot.Application.Appointments;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Patients;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Api;

public sealed class PatientCardTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DatabaseFixture _fixture;
    private readonly HttpClient _client;

    public PatientCardTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
        _client = fixture.Factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    private async Task<HttpResponseMessage> Get(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, Ct);
    }

    private Task<int> JournalEntriesFor(PatientId patient) =>
        _fixture.WithDbAsync(db => db.PatientCardAccess.CountAsync(a => a.PatientId == patient, Ct));

    private async Task<TestClinic> ClinicWithAppointment()
    {
        var clinic = await _fixture.CreateClinicAsync();
        (await _fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday, 10), BookingSource.Patient))).IsSuccess.ShouldBeTrue();
        return clinic;
    }

    [Fact]
    public async Task Doctor_reads_own_patient_card_and_it_is_journaled()
    {
        var clinic = await ClinicWithAppointment();

        var response = await Get($"/api/v1/patients/{clinic.Patient.Value}/card?purpose=appointment", await _fixture.DoctorTokenAsync(clinic.Doctor));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var card = (await response.Content.ReadFromJsonAsync<PatientCardResponse>(Json, Ct))!;
        card.FullName.ShouldBe("Пациент 1");
        card.Phone.ShouldBe("+79000000001");

        var log = await (await Get("/api/v1/me/card-access-log", await _fixture.AccessTokenAsync(clinic.Patient)))
            .Content.ReadFromJsonAsync<List<CardAccessResponse>>(Json, Ct);
        var entry = log!.ShouldHaveSingleItem();
        entry.AccessorKind.ShouldBe(CardAccessorKind.Doctor);
        entry.ActorName.ShouldBe("Тестовый Врач");
        entry.Purpose.ShouldBe("appointment");
        entry.AccessedAt.ShouldBe(BaseNow.ToDateTimeOffset());
    }

    [Fact]
    public async Task Doctor_cannot_read_card_of_someone_elses_patient()
    {
        var clinic = await ClinicWithAppointment();
        var other = await _fixture.CreateClinicAsync();

        var response = await Get($"/api/v1/patients/{clinic.Patient.Value}/card?purpose=curiosity", await _fixture.DoctorTokenAsync(other.Doctor));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await JournalEntriesFor(clinic.Patient)).ShouldBe(0);
    }

    [Fact]
    public async Task Purpose_is_required_and_nothing_is_returned_without_it()
    {
        var clinic = await ClinicWithAppointment();

        var response = await Get($"/api/v1/patients/{clinic.Patient.Value}/card", await _fixture.AdminTokenAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("+79000000001");
        (await JournalEntriesFor(clinic.Patient)).ShouldBe(0);
    }

    [Fact]
    public async Task Admin_reads_any_card_and_sees_the_journal()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var admin = await _fixture.AdminTokenAsync();

        (await Get($"/api/v1/patients/{clinic.Patient.Value}/card?purpose=support_request", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var log = await (await Get($"/api/v1/patients/{clinic.Patient.Value}/card-access-log", admin))
            .Content.ReadFromJsonAsync<List<CardAccessResponse>>(Json, Ct);
        var entry = log!.ShouldHaveSingleItem();
        entry.AccessorKind.ShouldBe(CardAccessorKind.Admin);
        entry.ActorName.ShouldBeNull();
        entry.Purpose.ShouldBe("support_request");
    }

    [Fact]
    public async Task Patient_sees_own_card_without_journal_entry_but_not_others()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        var token = await _fixture.AccessTokenAsync(clinic.Patient);

        var own = await Get("/api/v1/me/card", token);
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await own.Content.ReadFromJsonAsync<PatientCardResponse>(Json, Ct))!.PatientId.ShouldBe(clinic.Patient.Value);
        (await JournalEntriesFor(clinic.Patient)).ShouldBe(0);

        (await Get($"/api/v1/patients/{clinic.Patients[1].Value}/card?purpose=x", token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Journal_cannot_be_altered()
    {
        var clinic = await _fixture.CreateClinicAsync();
        (await Get($"/api/v1/patients/{clinic.Patient.Value}/card?purpose=audit_check", await _fixture.AdminTokenAsync()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var connection = new NpgsqlConnection(_fixture.ClinicConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in new[] { "UPDATE patient_card_access SET purpose = 'x'", "DELETE FROM patient_card_access" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct))).MessageText.ShouldContain("только пополняется");
        }

        (await JournalEntriesFor(clinic.Patient)).ShouldBe(1);
    }
}
