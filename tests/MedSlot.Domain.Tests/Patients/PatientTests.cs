using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Patients;
using static MedSlot.Domain.Tests.TestData;

namespace MedSlot.Domain.Tests.Patients;

public class BanTests
{
    private static readonly Instant Day0 = Instant.FromUtc(2026, 1, 1, 12, 0);

    private static Instant Day(double days) => Day0 + Duration.FromHours(days * 24);

    private static BanStatus Evaluate(Instant now, Instant? liftedAt = null, params double[] violationDays) =>
        PatientBookingPolicy.EvaluateBan(violationDays.Select(Day), liftedAt, now, Policy);

    [Fact]
    public void Two_violations_do_not_ban() =>
        Evaluate(Day(2), violationDays: [0, 1]).IsBannedAt(Day(2)).ShouldBeFalse();

    [Fact]
    public void Third_violation_in_window_bans_for_ban_duration_from_it()
    {
        var ban = Evaluate(Day(10), violationDays: [0, 1, 10]);

        ban.IsBannedAt(Day(10)).ShouldBeTrue();
        ban.BannedUntil.ShouldBe(Day(40));
    }

    [Fact]
    public void Ban_ends_exactly_after_ban_duration()
    {
        Evaluate(Day(40) - Duration.FromSeconds(1), violationDays: [0, 1, 10]).IsBannedAt(Day(40) - Duration.FromSeconds(1)).ShouldBeTrue();
        Evaluate(Day(40), violationDays: [0, 1, 10]).IsBannedAt(Day(40)).ShouldBeFalse();
    }

    [Fact]
    public void Violation_outside_window_does_not_count()
    {
        // Между первым и третьим ровно 90 дней: окно (третье − 90 дней, третье] первое не включает.
        Evaluate(Day(90), violationDays: [0, 50, 90]).IsBannedAt(Day(90)).ShouldBeFalse();
        Evaluate(Day(89.9), violationDays: [0, 50, 89.9]).IsBannedAt(Day(89.9)).ShouldBeTrue();
    }

    [Fact]
    public void Ban_persists_after_first_violation_leaves_window()
    {
        // Бан наступил на 85-й день и длится до 115-го, хотя на 95-й день в окне «от сейчас» уже только два нарушения.
        Evaluate(Day(95), violationDays: [0, 80, 85]).IsBannedAt(Day(95)).ShouldBeTrue();
    }

    [Fact]
    public void Later_trigger_extends_ban()
    {
        var ban = Evaluate(Day(20), violationDays: [0, 1, 2, 20]);

        ban.BannedUntil.ShouldBe(Day(50));
    }

    [Fact]
    public void Lifting_ban_ignores_earlier_violations()
    {
        var liftedAt = Day(11);

        Evaluate(Day(12), liftedAt, 0, 1, 10).IsBannedAt(Day(12)).ShouldBeFalse();
        Evaluate(Day(30), liftedAt, 0, 1, 10, 20, 25).IsBannedAt(Day(30)).ShouldBeFalse();
        Evaluate(Day(30), liftedAt, 0, 1, 10, 20, 25, 30).IsBannedAt(Day(30)).ShouldBeTrue();
    }

    [Fact]
    public void Future_violations_are_ignored() =>
        Evaluate(Day(5), violationDays: [0, 1, 10]).IsBannedAt(Day(5)).ShouldBeFalse();
}

public class BookingEligibilityTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 1, 10, 12, 0);

    private static readonly PatientBookingHistory Banned = new(
        [Now - Duration.FromDays(3), Now - Duration.FromDays(2), Now - Duration.FromDays(1)], null, 0);

    [Fact]
    public void Clean_patient_can_book() =>
        PatientBookingPolicy.CanBook(new PatientBookingHistory([], null, 0), BookingSource.Patient, Now, Policy).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Banned_patient_cannot_book_online_or_get_offers()
    {
        PatientBookingPolicy.CanBook(Banned, BookingSource.Patient, Now, Policy).Error.Code.ShouldBe("patient.booking_banned");
        PatientBookingPolicy.CanBook(Banned, BookingSource.Waitlist, Now, Policy).Error.Code.ShouldBe("patient.booking_banned");
    }

    [Fact]
    public void Admin_can_book_banned_patient_beyond_limit() =>
        PatientBookingPolicy.CanBook(Banned with { ActiveAppointments = 100 }, BookingSource.Admin, Now, Policy).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Active_appointments_limit()
    {
        var atLimit = new PatientBookingHistory([], null, Policy.MaxActiveAppointmentsPerPatient);
        var belowLimit = atLimit with { ActiveAppointments = Policy.MaxActiveAppointmentsPerPatient - 1 };

        PatientBookingPolicy.CanBook(belowLimit, BookingSource.Patient, Now, Policy).IsSuccess.ShouldBeTrue();
        PatientBookingPolicy.CanBook(atLimit, BookingSource.Patient, Now, Policy).Error.Code.ShouldBe("patient.too_many_active_appointments");
    }
}

public class PatientTests
{
    private static readonly PhoneNumber Phone = PhoneNumber.Create("+7 900 123-45-67").Value;

    [Fact]
    public void Create_patient()
    {
        var patient = Patient.Create(PatientId.New(), " Петров Пётр ", Phone, null).Value;

        patient.FullName.ShouldBe("Петров Пётр");
        patient.Phone.Value.ShouldBe("+79001234567");
        patient.BanLiftedAt.ShouldBeNull();
    }

    [Fact]
    public void Full_name_is_required() =>
        Patient.Create(PatientId.New(), "", Phone, null).Error.ShouldBe(PatientErrors.FullNameRequired);

    [Fact]
    public void Lift_ban_records_moment()
    {
        var patient = Patient.Create(PatientId.New(), "Петров Пётр", Phone, null).Value;
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);

        patient.LiftBan(now);

        patient.BanLiftedAt.ShouldBe(now);
    }

    [Theory]
    [InlineData("+7 (900) 123-45-67", "+79001234567")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public void Valid_phone_is_normalized(string raw, string expected) =>
        PhoneNumber.Create(raw).Value.ToString().ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("89001234567")] // без +
    [InlineData("+7 900 12")] // мало цифр
    [InlineData("+7 900 123 45 67 89 01 23")] // много цифр
    [InlineData("+7 900 abc 45 67")]
    public void Invalid_phone(string? raw) =>
        PhoneNumber.Create(raw).Error.ShouldBe(PatientErrors.InvalidPhoneNumber);

    [Theory]
    [InlineData(" Ivan@Example.COM ", "ivan@example.com")]
    [InlineData("a.b@mail.co.uk", "a.b@mail.co.uk")]
    public void Valid_email_is_normalized(string raw, string expected) =>
        Email.Create(raw).Value.ToString().ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("ivan")]
    [InlineData("@example.com")]
    [InlineData("ivan@@example.com")]
    [InlineData("ivan@example")]
    [InlineData("ivan@.com")]
    [InlineData("ivan@example.")]
    [InlineData("iv an@example.com")]
    public void Invalid_email(string? raw) =>
        Email.Create(raw).Error.ShouldBe(PatientErrors.InvalidEmail);
}
