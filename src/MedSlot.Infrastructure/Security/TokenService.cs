using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MedSlot.Application.Abstractions;
using MedSlot.Domain.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace MedSlot.Infrastructure.Security;

/// <summary>
/// Access-токен — JWT (HMAC-SHA256) с ролью и привязкой к пациенту или врачу; ПДн в нём нет.
/// Refresh-токен — 32 случайных байта; в БД хранится только SHA-256.
/// </summary>
internal sealed class TokenService(AuthOptions options) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(options.CreateSigningKey(), SecurityAlgorithms.HmacSha256);

    public Duration RefreshTokenLifetime => options.RefreshTokenLifetime;

    public AccessToken CreateAccessToken(UserAccount user, Instant now)
    {
        ArgumentNullException.ThrowIfNull(user);

        var claims = new Dictionary<string, object>
        {
            [MedSlotClaims.Subject] = user.Id.Value.ToString(),
            [MedSlotClaims.Role] = user.Role.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
        };
        if (user.PatientId is { } patientId)
        {
            claims[MedSlotClaims.PatientId] = patientId.Value.ToString();
        }

        if (user.DoctorId is { } doctorId)
        {
            claims[MedSlotClaims.DoctorId] = doctorId.Value.ToString();
        }

        var expiresAt = now + options.AccessTokenLifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.ToDateTimeUtc(),
            NotBefore = now.ToDateTimeUtc(),
            Expires = expiresAt.ToDateTimeUtc(),
            Claims = claims,
            SigningCredentials = _credentials,
        });

        return new AccessToken(token, expiresAt);
    }

    public IssuedRefreshToken CreateRefreshToken()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return new IssuedRefreshToken(token, HashRefreshToken(token));
    }

    public string HashRefreshToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
