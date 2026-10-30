using MedSlot.Domain.Common;

namespace MedSlot.Domain.Identity;

public readonly record struct RefreshTokenId(Guid Value)
{
    public static RefreshTokenId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum RefreshTokenUse
{
    /// <summary>Токен принят и погашен; клиент получит новый в том же семействе.</summary>
    Valid,

    Expired,

    Revoked,

    /// <summary>Погашенный токен предъявлен ещё раз — признак утечки (ADR 0011).</summary>
    Reused,
}

/// <summary>
/// Refresh-токен. Одноразовый: при обновлении погашается и заменяется новым из того же семейства.
/// Семейство — все токены одного входа. Хранится только хэш токена.
/// </summary>
public sealed class RefreshToken : AggregateRoot<RefreshTokenId>
{
    // Для EF Core.
    private RefreshToken()
        : base(default)
    {
        TokenHash = null!;
    }

    private RefreshToken(RefreshTokenId id, UserId userId, Guid familyId, string tokenHash, Instant now, Instant expiresAt)
        : base(id)
    {
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    public UserId UserId { get; private set; }

    /// <summary>Общий id всех токенов одного входа: при утечке отзывается всё семейство.</summary>
    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; }

    public Instant CreatedAt { get; private set; }

    public Instant ExpiresAt { get; private set; }

    public Instant? UsedAt { get; private set; }

    public Instant? RevokedAt { get; private set; }

    /// <summary>Первый токен нового входа — новое семейство.</summary>
    public static RefreshToken StartFamily(UserId userId, string tokenHash, Instant now, Duration lifetime) =>
        Issue(userId, Guid.CreateVersion7(), tokenHash, now, lifetime);

    /// <summary>Следующий токен того же входа после ротации.</summary>
    public RefreshToken Rotate(string tokenHash, Instant now, Duration lifetime) =>
        Issue(UserId, FamilyId, tokenHash, now, lifetime);

    /// <summary>
    /// Предъявление токена. Проверки по порядку: отозван → погашен (повтор) → истёк.
    /// Только для действующего токена отмечает погашение.
    /// </summary>
    public RefreshTokenUse Use(Instant now)
    {
        if (RevokedAt is not null)
        {
            return RefreshTokenUse.Revoked;
        }

        if (UsedAt is not null)
        {
            return RefreshTokenUse.Reused;
        }

        if (now >= ExpiresAt)
        {
            return RefreshTokenUse.Expired;
        }

        UsedAt = now;
        return RefreshTokenUse.Valid;
    }

    public void Revoke(Instant now) => RevokedAt ??= now;

    private static RefreshToken Issue(UserId userId, Guid familyId, string tokenHash, Instant now, Duration lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        return new RefreshToken(RefreshTokenId.New(), userId, familyId, tokenHash, now, now + lifetime);
    }
}
