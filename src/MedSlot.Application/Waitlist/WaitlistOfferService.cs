using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Patients;

namespace MedSlot.Application.Waitlist;

/// <summary>
/// Предлагает освободившееся время первому подходящему в листе ожидания (domain.md, §8).
/// Вызывается в той же единице работы, что и освобождение слота (отмена, отказ, истечение предложения):
/// слот сразу переходит в удержание, и обычная запись не может перехватить его между двумя шагами.
/// </summary>
internal sealed class WaitlistOfferService(
    IWaitlistRepository waitlist,
    IAppointmentRepository appointments,
    IServiceRepository services,
    IPatientRepository patients,
    IClock clock,
    ClinicPolicy policy)
{
    /// <summary>
    /// Ищет кандидата и создаёт для него удержание (Held). Ничего не сохраняет.
    /// Возвращает удержание или <c>null</c>, если предлагать некому или поздно.
    /// </summary>
    /// <param name="exclude">Заявка, которая только что отказалась от этого времени или пропустила его.</param>
    public async Task<Appointment?> OfferFreedSlotAsync(
        DoctorId doctorId, TimeRange freed, CancellationToken cancellationToken, WaitlistEntryId? exclude = null)
    {
        var now = clock.GetCurrentInstant();

        // Пациент должен успеть ответить, а после ответа — успеть прийти.
        if (freed.Start - now < policy.WaitlistOfferTtl + policy.MinBookingLeadTime)
        {
            return null;
        }

        var date = freed.Start.InZone(policy.TimeZone).Date;
        foreach (var entry in await waitlist.GetQueueAsync(doctorId, date, cancellationToken))
        {
            if (entry.Id == exclude)
            {
                continue;
            }

            var service = await services.GetByIdAsync(entry.ServiceId, cancellationToken);
            if (service is null || !entry.Fits(freed, service.Duration, policy.TimeZone))
            {
                continue;
            }

            var patient = await patients.GetByIdAsync(entry.PatientId, cancellationToken);
            if (patient is null || await IsBannedAsync(patient, now, cancellationToken))
            {
                continue;
            }

            var time = TimeRange.FromStart(freed.Start, service.Duration);
            if (await appointments.HasActiveOverlapAsync(patient.Id, time, cancellationToken))
            {
                continue;
            }

            var held = Appointment.Hold(AppointmentId.New(), entry.Id, doctorId, patient.Id, service.Id, time, now, policy);
            if (held.IsFailure || entry.MarkOffered(held.Value.Id).IsFailure)
            {
                return null;
            }

            appointments.Add(held.Value);
            return held.Value;
        }

        return null;
    }

    /// <summary>
    /// Ошибка сохранения единицы работы, в которой делалось предложение. Если кандидат параллельно
    /// записался на это же время, constraint пациента отклонит удержание вместе со всей операцией.
    /// Клиент получит «данные изменились» и повторит: при повторе кандидат уже не пройдёт проверку занятости.
    /// </summary>
    public static DomainError SaveError(DomainError error, Appointment? offer) =>
        offer is not null && error == AppointmentErrors.PatientTimeConflict ? ApplicationErrors.ConcurrentModification : error;

    private async Task<bool> IsBannedAsync(Patient patient, Instant now, CancellationToken cancellationToken)
    {
        var violations = await appointments.GetViolationTimesAsync(
            patient.Id, now - policy.ViolationWindow - policy.BanDuration, cancellationToken);
        return PatientBookingPolicy.EvaluateBan(violations, patient.BanLiftedAt, now, policy).IsBannedAt(now);
    }
}
