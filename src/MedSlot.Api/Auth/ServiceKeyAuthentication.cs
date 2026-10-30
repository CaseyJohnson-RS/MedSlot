using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MedSlot.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MedSlot.Api.Auth;

/// <summary>Ключи внутренних сервисов: имя сервиса → ключ. Задаются в конфигурации <c>Internal:ServiceKeys</c>.</summary>
internal sealed class ServiceKeyOptions : AuthenticationSchemeOptions
{
    public Dictionary<string, string> Keys { get; set; } = [];
}

/// <summary>
/// Аутентификация внутренних сервисов по заголовку <c>X-Service-Key</c> (ADR 0012).
/// Ключ сравнивается за постоянное время; при успехе — роль <c>Service</c> и имя сервиса в claim.
/// </summary>
internal sealed class ServiceKeyAuthenticationHandler(
    IOptionsMonitor<ServiceKeyOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<ServiceKeyOptions>(options, logger, encoder)
{
    public const string SchemeName = "ServiceKey";
    public const string HeaderName = "X-Service-Key";
    public const string ServiceClaim = "service";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header) || header.ToString() is not { Length: > 0 } presented)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        foreach (var (service, key) in Options.Keys)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedBytes, Encoding.UTF8.GetBytes(key)))
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ServiceClaim, service), new Claim(MedSlotClaims.Role, Policies.Service)],
                    SchemeName,
                    ServiceClaim,
                    MedSlotClaims.Role);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        return Task.FromResult(AuthenticateResult.Fail("Неизвестный ключ сервиса."));
    }
}
