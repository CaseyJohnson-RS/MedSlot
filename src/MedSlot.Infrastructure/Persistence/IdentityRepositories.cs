using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace MedSlot.Infrastructure.Persistence;

internal sealed class UserRepository(MedSlotDbContext db) : IUserRepository
{
    public Task<UserAccount?> GetByIdAsync(UserId id, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<UserAccount?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Email == email, cancellationToken);

    public Task<bool> DoctorHasAccountAsync(DoctorId doctorId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.DoctorId == doctorId, cancellationToken);

    public void Add(UserAccount account) => db.Users.Add(account);
}

internal sealed class RefreshTokenRepository(MedSlotDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public Task RevokeFamilyAsync(Guid familyId, Instant now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
}
