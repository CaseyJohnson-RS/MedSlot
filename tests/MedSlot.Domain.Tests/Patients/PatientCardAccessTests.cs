using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;

namespace MedSlot.Domain.Tests.Patients;

public class PatientCardAccessTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 2, 9, 0);

    [Fact]
    public void Doctor_access_records_who_and_why()
    {
        var patient = PatientId.New();
        var user = UserId.New();
        var doctor = DoctorId.New();

        var access = PatientCardAccess.ByDoctor(patient, user, doctor, " appointment ", Now).Value;

        access.PatientId.ShouldBe(patient);
        access.AccessorKind.ShouldBe(CardAccessorKind.Doctor);
        access.AccessorUserId.ShouldBe(user);
        access.DoctorId.ShouldBe(doctor);
        access.Purpose.ShouldBe("appointment");
        access.AccessedAt.ShouldBe(Now);
    }

    [Fact]
    public void Service_access_has_no_user()
    {
        var access = PatientCardAccess.ByService(PatientId.New(), "notifications", "notification", Now).Value;

        access.AccessorKind.ShouldBe(CardAccessorKind.Service);
        access.ServiceName.ShouldBe("notifications");
        access.AccessorUserId.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Purpose_is_required(string purpose) =>
        PatientCardAccess.ByAdmin(PatientId.New(), UserId.New(), purpose, Now).Error.ShouldBe(PatientErrors.CardAccessPurposeRequired);

    [Fact]
    public void Purpose_is_limited() =>
        PatientCardAccess.ByAdmin(PatientId.New(), UserId.New(), new string('x', PatientCardAccess.MaxPurposeLength + 1), Now)
            .Error.ShouldBe(PatientErrors.CardAccessPurposeRequired);
}
