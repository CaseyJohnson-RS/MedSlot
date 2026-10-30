using System.Text;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace MedSlot.Infrastructure.Security;

public sealed class AuthOptions
{
    public string Issuer { get; init; } = "medslot";

    public string Audience { get; init; } = "medslot-api";

    /// <summary>Ключ подписи HMAC-SHA256, не короче 32 байт. В продакшене — из секретов, не из appsettings.</summary>
    public required string SigningKey { get; init; }

    public Duration AccessTokenLifetime { get; init; } = Duration.FromMinutes(15);

    public Duration RefreshTokenLifetime { get; init; } = Duration.FromDays(30);

    public SymmetricSecurityKey CreateSigningKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        return bytes.Length >= 32
            ? new SymmetricSecurityKey(bytes)
            : throw new InvalidOperationException("Ключ подписи токенов должен быть не короче 32 байт.");
    }

    /// <summary>
    /// Параметры проверки токена. Срок жизни сверяется с <see cref="IClock"/> приложения, а не с системными часами:
    /// одно время для выпуска и проверки (и для тестов с подменёнными часами).
    /// </summary>
    public TokenValidationParameters CreateValidationParameters(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = CreateSigningKey(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = Application.Abstractions.MedSlotClaims.Subject,
            RoleClaimType = Application.Abstractions.MedSlotClaims.Role,
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = clock.GetCurrentInstant().ToDateTimeUtc();
                var skew = TimeSpan.FromSeconds(30);
                return (notBefore is null || notBefore <= now + skew) && expires is not null && expires > now - skew;
            },
        };
    }
}
