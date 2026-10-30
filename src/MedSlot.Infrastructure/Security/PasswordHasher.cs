using MedSlot.Application.Abstractions;
using MedSlot.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using IdentityPasswordHasher = Microsoft.AspNetCore.Identity.PasswordHasher<MedSlot.Domain.Identity.UserAccount>;

namespace MedSlot.Infrastructure.Security;

/// <summary>
/// PBKDF2 (HMAC-SHA512, 100 000 итераций, соль) из ASP.NET Core Identity. Формат хэша версионирован:
/// при смене параметров старые хэши распознаются и пересчитываются при входе.
/// </summary>
internal sealed class PasswordHasher : IPasswordHasher
{
    private readonly IdentityPasswordHasher _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public PasswordCheck Verify(string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(null!, passwordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
}
