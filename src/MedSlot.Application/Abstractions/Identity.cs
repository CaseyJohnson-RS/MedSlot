using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;

namespace MedSlot.Application.Abstractions;

public interface IUserRepository
{
    Task<UserAccount?> GetByIdAsync(UserId id, CancellationToken cancellationToken);

    Task<UserAccount?> GetByEmailAsync(Email email, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken);

    Task<bool> DoctorHasAccountAsync(DoctorId doctorId, CancellationToken cancellationToken);

    void Add(UserAccount account);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken);

    void Add(RefreshToken token);

    /// <summary>Отзывает все токены семейства сразу, не дожидаясь SaveChanges.</summary>
    Task RevokeFamilyAsync(Guid familyId, Instant now, CancellationToken cancellationToken);
}

public enum PasswordCheck
{
    Failed,
    Success,

    /// <summary>Пароль верный, но хэш создан со старыми параметрами — стоит пересчитать.</summary>
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string passwordHash, string password);
}

/// <summary>Claims access-токена: их выпускает инфраструктура, а читает API.</summary>
public static class MedSlotClaims
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string PatientId = "patient_id";
    public const string DoctorId = "doctor_id";
}

public sealed record AccessToken(string Token, Instant ExpiresAt);

/// <summary>Новый refresh-токен: сам токен уходит клиенту, в БД — только хэш.</summary>
public sealed record IssuedRefreshToken(string Token, string Hash);

public interface ITokenService
{
    Duration RefreshTokenLifetime { get; }

    AccessToken CreateAccessToken(UserAccount user, Instant now);

    IssuedRefreshToken CreateRefreshToken();

    string HashRefreshToken(string token);
}

/// <summary>Выданная сессия: короткоживущий access-токен и одноразовый refresh-токен.</summary>
public sealed record AuthTokens(
    string AccessToken,
    Instant AccessTokenExpiresAt,
    string RefreshToken,
    Instant RefreshTokenExpiresAt,
    UserRole Role);
