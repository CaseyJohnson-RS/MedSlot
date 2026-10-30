using MedSlot.Domain.Appointments;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;

namespace MedSlot.Domain.Patients;

/// <summary>Сведения о пациенте, нужные для решения, можно ли ему записаться.</summary>
/// <param name="ViolationTimes">Моменты нарушений: записи в статусах LateCancelled и NoShow.</param>
/// <param name="BanLiftedAt">Момент досрочного снятия бана администратором.</param>
/// <param name="ActiveAppointments">Число активных записей (Held, Booked).</param>
public sealed record PatientBookingHistory(
    IReadOnlyCollection<Instant> ViolationTimes,
    Instant? BanLiftedAt,
    int ActiveAppointments);

public sealed record BanStatus(Instant? BannedUntil)
{
    public static BanStatus NotBanned { get; } = new((Instant?)null);

    public bool IsBannedAt(Instant now) => BannedUntil > now;
}

/// <summary>Правила допуска пациента к записи: бан и лимит активных записей (domain.md, §7; ADR 0004).</summary>
public static class PatientBookingPolicy
{
    /// <summary>
    /// Бан наступает в момент нарушения, если в окне <c>ViolationWindow</c>, которое заканчивается этим
    /// нарушением, набралось не меньше <c>ViolationsForBan</c> нарушений. Длится <c>BanDuration</c> от него.
    /// </summary>
    public static BanStatus EvaluateBan(
        IEnumerable<Instant> violationTimes,
        Instant? banLiftedAt,
        Instant now,
        ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(violationTimes);
        ArgumentNullException.ThrowIfNull(policy);

        var threshold = policy.ViolationsForBan;
        var violations = violationTimes
            .Where(v => v <= now && (banLiftedAt is null || v > banLiftedAt))
            .Order()
            .ToList();

        Instant? bannedUntil = null;
        for (var i = threshold - 1; i < violations.Count; i++)
        {
            // Нарушения отсортированы: в окне, заканчивающемся на i-м, их не меньше threshold,
            // если первое из последних threshold попадает в окно.
            var windowStart = violations[i] - policy.ViolationWindow;
            if (violations[i - threshold + 1] > windowStart)
            {
                bannedUntil = violations[i] + policy.BanDuration;
            }
        }

        return bannedUntil > now ? new BanStatus(bannedUntil) : BanStatus.NotBanned;
    }

    public static Result CanBook(PatientBookingHistory history, BookingSource source, Instant now, ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);

        // Администратор записывает пациента без ограничений (domain.md, §6.2).
        if (source == BookingSource.Admin)
        {
            return Result.Success();
        }

        var ban = EvaluateBan(history.ViolationTimes, history.BanLiftedAt, now, policy);
        if (ban.IsBannedAt(now))
        {
            return PatientErrors.BookingBanned(ban.BannedUntil!.Value);
        }

        if (source == BookingSource.Patient && history.ActiveAppointments >= policy.MaxActiveAppointmentsPerPatient)
        {
            return PatientErrors.TooManyActiveAppointments(policy.MaxActiveAppointmentsPerPatient);
        }

        return Result.Success();
    }
}
