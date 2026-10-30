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
            foreach (var interval in doctor.WorkingHoursOn(date).Intervals)
            {
                if (interval.ToTimeRange(date, policy.TimeZone) is not { } working)
                {
                    continue;
                }

                for (var start = working.Start; start + service.Duration <= working.End; start += policy.SlotGridStep)
                {
                    if (start > latestStart)
                    {
                        // Даты и интервалы идут по возрастанию: дальше будет только позже горизонта.
                        return Result.Success<IReadOnlyList<TimeRange>>(slots);
                    }

                    if (start < earliestStart)
                    {
                        continue;
                    }

                    var candidate = TimeRange.FromStart(start, service.Duration);
                    if (!occupied.Exists(o => o.Overlaps(candidate)))
                    {
                        slots.Add(candidate);
                    }
                }
            }
        }

        return Result.Success<IReadOnlyList<TimeRange>>(slots);
    }
}
