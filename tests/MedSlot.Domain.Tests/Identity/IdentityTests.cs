using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;

namespace MedSlot.Domain.Tests.Identity;

public class RefreshTokenTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 2, 9, 0);
    private static readonly Duration Lifetime = Duration.FromDays(30);

    [Fact]
    public void Token_can_be_used_once()
    {
        var token = RefreshToken.StartFamily(UserId.New(), "hash-1", Now, Lifetime);

        token.Use(Now).ShouldBe(RefreshTokenUse.Valid);
        token.UsedAt.ShouldBe(Now);

        // Повторное предъявление — признак утечки.
        token.Use(Now + Duration.FromMinutes(1)).ShouldBe(RefreshTokenUse.Reused);
    }

    [Fact]
    public void Rotation_keeps_family_and_user()
    {
        var first = RefreshToken.StartFamily(UserId.New(), "hash-1", Now, Lifetime);

        var second = first.Rotate("hash-2", Now + Duration.FromDays(1), Lifetime);

        second.FamilyId.ShouldBe(first.FamilyId);
        second.UserId.ShouldBe(first.UserId);
        second.Id.ShouldNotBe(first.Id);
        second.ExpiresAt.ShouldBe(Now + Duration.FromDays(31));
    }

    [Fact]
    public void New_login_starts_new_family()
    {
        var user = UserId.New();

        RefreshToken.StartFamily(user, "a", Now, Lifetime).FamilyId
            .ShouldNotBe(RefreshToken.StartFamily(user, "b", Now, Lifetime).FamilyId);
    }

    [Fact]
    public void Expiry_boundary_is_exclusive()
    {
        RefreshToken.StartFamily(UserId.New(), "h", Now, Lifetime).Use(Now + Lifetime - Duration.FromSeconds(1)).ShouldBe(RefreshTokenUse.Valid);
        RefreshToken.StartFamily(UserId.New(), "h", Now, Lifetime).Use(Now + Lifetime).ShouldBe(RefreshTokenUse.Expired);
    }

    [Fact]
    public void Revoked_token_is_rejected_even_if_unused()
    {
        var token = RefreshToken.StartFamily(UserId.New(), "h", Now, Lifetime);

        token.Revoke(Now);
        token.Revoke(Now + Duration.FromHours(1)); // повторный отзыв не меняет момент

        token.RevokedAt.ShouldBe(Now);
        token.Use(Now).ShouldBe(RefreshTokenUse.Revoked);
        token.UsedAt.ShouldBeNull();
    }
}

public class UserAccountTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 2, 9, 0);
    private static readonly Email Login = Email.Create("user@example.com").Value;

    [Fact]
    public void Patient_account_is_linked_to_patient()
    {
        var patient = PatientId.New();

        var account = UserAccount.ForPatient(UserId.New(), Login, "hash", patient, Now);

        account.Role.ShouldBe(UserRole.Patient);
        account.PatientId.ShouldBe(patient);
        account.DoctorId.ShouldBeNull();
    }

    [Fact]
    public void Doctor_account_requires_doctor()
    {
        var doctor = DoctorId.New();

        UserAccount.ForStaff(UserId.New(), Login, "hash", UserRole.Doctor, doctor, Now).Value.DoctorId.ShouldBe(doctor);
        UserAccount.ForStaff(UserId.New(), Login, "hash", UserRole.Doctor, null, Now).Error.ShouldBe(IdentityErrors.DoctorRequired);
    }

    [Fact]
    public void Admin_account_is_not_linked_to_doctor()
    {
        UserAccount.ForStaff(UserId.New(), Login, "hash", UserRole.Admin, null, Now).Value.Role.ShouldBe(UserRole.Admin);
        UserAccount.ForStaff(UserId.New(), Login, "hash", UserRole.Admin, DoctorId.New(), Now).Error.ShouldBe(IdentityErrors.AdminCannotBeDoctor);
    }

    [Fact]
    public void Staff_cannot_have_patient_role() =>
        UserAccount.ForStaff(UserId.New(), Login, "hash", UserRole.Patient, null, Now).Error.ShouldBe(IdentityErrors.NotAStaffRole);

    [Fact]
    public void Password_hash_can_be_upgraded()
    {
        var account = UserAccount.ForPatient(UserId.New(), Login, "old", PatientId.New(), Now);

        account.ReplacePasswordHash("new");

        account.PasswordHash.ShouldBe("new");
    }
}
