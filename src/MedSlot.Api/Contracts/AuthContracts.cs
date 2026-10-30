using MedSlot.Application.Abstractions;
using MedSlot.Domain.Identity;

namespace MedSlot.Api.Contracts;

public sealed record RegisterRequest(string FullName, string Phone, string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>
/// Сессия: <c>AccessToken</c> передаётся в заголовке <c>Authorization: Bearer …</c> и живёт 15 минут;
/// <c>RefreshToken</c> одноразовый — обменивается на новую пару в <c>/auth/refresh</c>.
/// </summary>
public sealed record TokensResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserRole Role,
    string TokenType = "Bearer")
{
    internal static TokensResponse Create(AuthTokens tokens) => new(
        tokens.AccessToken,
        tokens.AccessTokenExpiresAt.ToDateTimeOffset(),
        tokens.RefreshToken,
        tokens.RefreshTokenExpiresAt.ToDateTimeOffset(),
        tokens.Role);
}
