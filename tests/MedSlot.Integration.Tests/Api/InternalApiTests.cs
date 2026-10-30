using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MedSlot.Api.Contracts;
using MedSlot.Domain.Common;
using MedSlot.Domain.Patients;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Api;

public sealed class InternalApiTests : IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly HttpClient _client;

    public InternalApiTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
        _client = fixture.Factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    private async Task<HttpResponseMessage> Contacts(PatientId patient, string? serviceKey = null, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/patients/{patient.Value}/contacts?purpose=notification");
        if (serviceKey is not null)
        {
            request.Headers.Add("X-Service-Key", serviceKey);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Service_reads_contacts_and_access_is_journaled()
    {
        var clinic = await _fixture.CreateClinicAsync();

        var response = await Contacts(clinic.Patient, NotificationsServiceKey);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PatientCardResponse>(Ct))!.Phone.ShouldBe("+79000000001");

        var entry = await _fixture.WithDbAsync(db => db.PatientCardAccess.AsNoTracking().SingleAsync(a => a.PatientId == clinic.Patient, Ct));
        entry.AccessorKind.ShouldBe(CardAccessorKind.Service);
        entry.ServiceName.ShouldBe("notifications");
        entry.Purpose.ShouldBe("notification");
    }

    [Fact]
    public async Task Without_valid_service_key_access_is_denied()
    {
        var clinic = await _fixture.CreateClinicAsync();

        (await Contacts(clinic.Patient)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Contacts(clinic.Patient, "wrong-key")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Токен пользователя, даже администратора, внутренний API не открывает.
        (await Contacts(clinic.Patient, bearer: await _fixture.AdminTokenAsync())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await _fixture.WithDbAsync(db => db.PatientCardAccess.CountAsync(a => a.PatientId == clinic.Patient, Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Internal_api_is_not_in_public_documentation()
    {
        var document = await _client.GetStringAsync("/openapi/v1.json", Ct);

        document.ShouldNotContain("/internal/");
    }
}
