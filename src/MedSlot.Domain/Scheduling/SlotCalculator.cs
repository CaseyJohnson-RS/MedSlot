using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Services;

namespace MedSlot.Domain.Scheduling;

/// <summary>Вычисляет свободные слоты врача для услуги (алгоритм — domain.md, §5).</summary>
public static class SlotCalculator
{
    /// <param name="doctor">Врач с расписанием и исключениями.</param>
    /// <param name="service">Услуга: её длительность определяет длину слота.</param>
    /// <param name="dates">Даты по календарю клиники, включительно.</param>
    /// <param name="busy">Интервалы активных записей врача (<c>Held</c>, <c>Booked</c>).</param>
    /// <param name="now">Текущий момент.</param>
    /// <param name="policy">Параметры клиники.</param>
    public static Result<IReadOnlyList<TimeRange>> Calculate(
        Doctor doctor,
        Service service,
        DateInterval dates,
        IEnumerable<TimeRange> busy,
        Instant now,
        ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(busy);
        ArgumentNullException.ThrowIfNull(policy);

        if (!doctor.Provides(service.Id))
        {
            return DoctorErrors.ServiceNotProvided;
        }

        var earliestStart = now + policy.MinBookingLeadTime;
        var latestStart = now + policy.BookingHorizon;
        var occupied = busy.ToList();
        var slots = new List<TimeRange>();

        foreach (var date in dates)
        {
            foreach (var candidate in ScheduledSlots(doctor, date, service.Duration, policy))
            {
                if (candidate.Start > latestStart)
                {
                    // Даты и интервалы идут по возрастанию: дальше будет только позже горизонта.
                    return Result.Success<IReadOnlyList<TimeRange>>(slots);
                }

                if (candidate.Start >= earliestStart && !occupied.Exists(o => o.Overlaps(candidate)))
                {
                    slots.Add(candidate);
                }
            }
        }

        return Result.Success<IReadOnlyList<TimeRange>>(slots);
    }

    /// <summary>
    /// Попадает ли приём, начинающийся в <paramref name="start"/>, в сетку расписания врача.
    /// Занятость, горизонт и минимальное время до записи не проверяются.
    /// </summary>
    public static bool IsWithinSchedule(Doctor doctor, Service service, Instant start, ClinicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(policy);

        // Смены через полночь не поддерживаются, поэтому приём принадлежит дате своего начала.
        var date = start.InZone(policy.TimeZone).Date;
        return ScheduledSlots(doctor, date, service.Duration, policy).Any(s => s.Start == start);
    }

    /// <summary>Все слоты по сетке расписания на дату, без учёта занятости и ограничений по времени.</summary>
    private static IEnumerable<TimeRange> ScheduledSlots(Doctor doctor, LocalDate date, Duration duration, ClinicPolicy policy)
    {
        foreach (var interval in doctor.WorkingHoursOn(date).Intervals)
        {
            if (interval.ToTimeRange(date, policy.TimeZone) is not { } working)
            {
                continue;
            }

            for (var start = working.Start; start + duration <= working.End; start += policy.SlotGridStep)
            {
                yield return TimeRange.FromStart(start, duration);
            }
        }
    }
}
