using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MedSlot.Api.Contracts;
using MedSlot.Domain.Identity;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Api;

public sealed class AuthTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DatabaseFixture _fixture;
    private readonly HttpClient _client;

    public AuthTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
        _client = fixture.Factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private async Task<TokensResponse> Register(string email, string password = "Secret-123")
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auth/register", new RegisterRequest("Иван Пациентов", "+7 900 555-00-11", email, password), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TokensResponse>(Json, Ct))!;
    }

    private Task<HttpResponseMessage> Refresh(string refreshToken) =>
        _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken), Ct);

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();

    [Fact]
    public async Task Registration_creates_patient_and_opens_session()
    {
        var email = NewEmail();

        var tokens = await Register(email);

        tokens.Role.ShouldBe(UserRole.Patient);
        tokens.TokenType.ShouldBe("Bearer");
        tokens.AccessTokenExpiresAt.ShouldBe((_fixture.Clock.GetCurrentInstant() + Duration.FromMinutes(15)).ToDateTimeOffset());
        tokens.RefreshTokenExpiresAt.ShouldBe((_fixture.Clock.GetCurrentInstant() + Duration.FromDays(30)).ToDateTimeOffset());

        var account = await _fixture.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == MedSlot.Domain.Patients.Email.Create(email).Value, Ct));
        account.PasswordHash.ShouldNotContain("Secret-123");
        var patient = await _fixture.WithDbAsync(db => db.Patients.AsNoTracking().SingleAsync(p => p.Id == account.PatientId, Ct));
        patient.FullName.ShouldBe("Иван Пациентов");
    }

    [Fact]
    public async Task Email_can_be_registered_once()
    {
        var email = NewEmail();
        await Register(email);

        var again = await _client.PostAsJsonAsync(
            "/api/v1/auth/register", new RegisterRequest("Другой", "+7 900 555-00-12", email.ToUpperInvariant(), "Other-123"), Ct);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCode(again)).ShouldBe("auth.email_taken");
    }

    [Fact]
    public async Task Weak_password_is_rejected() =>
        (await _client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest("Иван", "+7 900 555-00-13", NewEmail(), "short"), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Login_checks_password_without_revealing_which_part_is_wrong()
    {
        var email = NewEmail();
        await Register(email);

        var ok = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Secret-123"), Ct);
        var wrongPassword = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Wrong-123"), Ct);
        var unknownEmail = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(NewEmail(), "Secret-123"), Ct);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCode(wrongPassword)).ShouldBe("auth.invalid_credentials");
        (await ErrorCode(unknownEmail)).ShouldBe("auth.invalid_credentials");
    }

    [Fact]
    public async Task Refresh_rotates_tokens()
    {
        var first = await Register(NewEmail());

        var response = await Refresh(first.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await response.Content.ReadFromJsonAsync<TokensResponse>(Json, Ct))!;
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        (await Refresh(second.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reused_refresh_token_revokes_the_whole_family()
    {
        var stolen = await Register(NewEmail());
        var legitimate = (await (await Refresh(stolen.RefreshToken)).Content.ReadFromJsonAsync<TokensResponse>(Json, Ct))!;

        // Украденный токен предъявлен повторно: уже погашен.
        var reuse = await Refresh(stolen.RefreshToken);
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCode(reuse)).ShouldBe("auth.refresh_token_reused");

        // Семейство отозвано: и свежий токен владельца больше не работает.
        var afterRevocation = await Refresh(legitimate.RefreshToken);
        afterRevocation.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCode(afterRevocation)).ShouldBe("auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Other_logins_of_the_same_user_survive_reuse_detection()
    {
        var email = NewEmail();
        var phone = await Register(email);
        var laptop = (await (await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Secret-123"), Ct))
            .Content.ReadFromJsonAsync<TokensResponse>(Json, Ct))!;

        await Refresh(phone.RefreshToken);
        await Refresh(phone.RefreshToken); // повтор — отзыв семейства «телефона»

        (await Refresh(laptop.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var tokens = await Register(NewEmail());

        _fixture.Clock.Advance(Duration.FromDays(30));

        (await ErrorCode(await Refresh(tokens.RefreshToken))).ShouldBe("auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_revokes_session()
    {
        var tokens = await Register(NewEmail());

        (await _client.PostAsJsonAsync("/api/v1/auth/logout", new RefreshRequest(tokens.RefreshToken), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Refresh(tokens.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_refresh_token_is_rejected() =>
        (await ErrorCode(await Refresh("not-a-real-token"))).ShouldBe("auth.invalid_refresh_token");
}
