using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Waitlist;

namespace MedSlot.Application.Waitlist;

/// <summary>Встать в лист ожидания к врачу на услугу в диапазоне дат <c>From</c>–<c>To</c> (включительно).</summary>
public sealed record JoinWaitlist(PatientId PatientId, DoctorId DoctorId, ServiceId ServiceId, LocalDate From, LocalDate To)
    : ICommand<WaitlistEntryId>;

/// <summary>Принять предложенное время. <c>AppointmentId</c> — id удержания.</summary>
public sealed record AcceptWaitlistOffer(AppointmentId AppointmentId, PatientId PatientId) : ICommand<Unit>;

/// <summary>Отказаться от предложенного времени; заявка остаётся в очереди.</summary>
public sealed record DeclineWaitlistOffer(AppointmentId AppointmentId, PatientId PatientId) : ICommand<Unit>;

public sealed record CancelWaitlistEntry(WaitlistEntryId EntryId, PatientId PatientId) : ICommand<Unit>;

/// <summary>Снять одно истёкшее предложение и предложить время следующему. <c>true</c> — было что снимать.</summary>
public sealed record ExpireNextWaitlistOffer : ICommand<bool>;

/// <summary>Закрыть заявки, чей диапазон дат уже прошёл. Возвращает число закрытых.</summary>
public sealed record ExpireStaleWaitlistEntries : ICommand<int>;

internal sealed class JoinWaitlistHandler(
    IDoctorRepository doctors,
    IServiceRepository services,
    IPatientRepository patients,
    IAppointmentRepository appointments,
    IWaitlistRepository waitlist,
    IUnitOfWork unitOfWork,
    IClock clock,
    ClinicPolicy policy) : ICommandHandler<JoinWaitlist, WaitlistEntryId>
{
    public async Task<Result<WaitlistEntryId>> Handle(JoinWaitlist command, CancellationToken cancellationToken)
    {
        var doctor = await doctors.GetByIdAsync(command.DoctorId, cancellationToken);
        if (doctor is null)
        {
            return ApplicationErrors.NotFound("doctor", command.DoctorId);
        }

        if (!doctor.Provides(command.ServiceId))
        {
            return await services.GetByIdAsync(command.ServiceId, cancellationToken) is null
                ? ApplicationErrors.NotFound("service", command.ServiceId)
                : DoctorErrors.ServiceNotProvided;
        }

        var patient = await patients.GetByIdAsync(command.PatientId, cancellationToken);
        if (patient is null)
        {
            return ApplicationErrors.NotFound("patient", command.PatientId);
        }

        var now = clock.GetCurrentInstant();
        var violations = await appointments.GetViolationTimesAsync(
            patient.Id, now - policy.ViolationWindow - policy.BanDuration, cancellationToken);
        var ban = PatientBookingPolicy.EvaluateBan(violations, patient.BanLiftedAt, now, policy);
        if (ban.IsBannedAt(now))
        {
            return PatientErrors.BookingBanned(ban.BannedUntil!.Value);
        }

        if (await waitlist.HasOpenAsync(patient.Id, doctor.Id, command.ServiceId, cancellationToken))
        {
            return WaitlistErrors.DuplicateEntry;
        }

        if (await waitlist.CountOpenAsync(patient.Id, cancellationToken) >= policy.MaxWaitlistEntriesPerPatient)
        {
            return WaitlistErrors.TooManyEntries(policy.MaxWaitlistEntriesPerPatient);
        }

        if (command.To < command.From)
        {
            return ApplicationErrors.InvalidDateRange(policy.BookingHorizon.Days);
        }

        var entry = WaitlistEntry.Join(
            WaitlistEntryId.New(), patient.Id, doctor.Id, command.ServiceId, new DateInterval(command.From, command.To), now, policy);
        if (entry.IsFailure)
        {
            return entry.Error;
        }

        waitlist.Add(entry.Value);
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? entry.Value.Id : saved.Error;
    }
}

internal sealed class AcceptWaitlistOfferHandler(
    IAppointmentRepository appointments, IWaitlistRepository waitlist, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<AcceptWaitlistOffer, Unit>
{
    public async Task<Result<Unit>> Handle(AcceptWaitlistOffer command, CancellationToken cancellationToken)
    {
        var offer = await WaitlistOffers.FindOwnAsync(appointments, waitlist, command.AppointmentId, command.PatientId, cancellationToken);
        if (offer.IsFailure)
        {
            return offer.Error;
        }

        var (appointment, entry) = offer.Value;
        var accepted = appointment.AcceptOffer(clock.GetCurrentInstant());
        if (accepted.IsFailure)
        {
            return accepted.Error;
        }

        // Единственное место, где в одной транзакции меняются два агрегата (domain.md, §3).
        var fulfilled = entry.Fulfill();
        if (fulfilled.IsFailure)
        {
            return fulfilled.Error;
        }

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}

internal sealed class DeclineWaitlistOfferHandler(
    IAppointmentRepository appointments,
    IWaitlistRepository waitlist,
    WaitlistOfferService offers,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<DeclineWaitlistOffer, Unit>
{
    public async Task<Result<Unit>> Handle(DeclineWaitlistOffer command, CancellationToken cancellationToken)
    {
        var offer = await WaitlistOffers.FindOwnAsync(appointments, waitlist, command.AppointmentId, command.PatientId, cancellationToken);
        if (offer.IsFailure)
        {
            return offer.Error;
        }

        var (appointment, entry) = offer.Value;
        var now = clock.GetCurrentInstant();
        var declined = appointment.DeclineOffer(now);
        if (declined.IsFailure)
        {
            return declined.Error;
        }

        entry.OfferDeclined(now);
        var next = await offers.OfferFreedSlotAsync(appointment.DoctorId, appointment.Time, cancellationToken, exclude: entry.Id);

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? Unit.Value : WaitlistOfferService.SaveError(saved.Error, next);
    }
}

internal sealed class CancelWaitlistEntryHandler(
    IAppointmentRepository appointments,
    IWaitlistRepository waitlist,
    WaitlistOfferService offers,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<CancelWaitlistEntry, Unit>
{
    public async Task<Result<Unit>> Handle(CancelWaitlistEntry command, CancellationToken cancellationToken)
    {
        var entry = await waitlist.GetByIdAsync(command.EntryId, cancellationToken);
        if (entry is null || entry.PatientId != command.PatientId)
        {
            return ApplicationErrors.NotFound("waitlist", command.EntryId);
        }

        // Если по заявке ждёт предложение — снимаем удержание и отдаём время следующему.
        Appointment? next = null;
        if (entry.CurrentOfferId is { } offerId
            && await appointments.GetByIdAsync(offerId, cancellationToken) is { Status: AppointmentStatus.Held } held)
        {
            held.DeclineOffer(clock.GetCurrentInstant());
            next = await offers.OfferFreedSlotAsync(held.DoctorId, held.Time, cancellationToken, exclude: entry.Id);
        }

        var cancelled = entry.Cancel();
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? Unit.Value : WaitlistOfferService.SaveError(saved.Error, next);
    }
}

internal sealed class ExpireNextWaitlistOfferHandler(
    IAppointmentRepository appointments,
    IWaitlistRepository waitlist,
    WaitlistOfferService offers,
    IUnitOfWork unitOfWork,
    IClock clock,
    ClinicPolicy policy) : ICommandHandler<ExpireNextWaitlistOffer, bool>
{
    public async Task<Result<bool>> Handle(ExpireNextWaitlistOffer command, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var held = await appointments.GetNextExpiredHoldAsync(now, cancellationToken);
        if (held is null)
        {
            return false;
        }

        var expired = held.ExpireOffer(now);
        if (expired.IsFailure)
        {
            return expired.Error;
        }

        WaitlistEntryId? entryId = null;
        if (held.WaitlistEntryId is { } id && await waitlist.GetByIdAsync(id, cancellationToken) is { } entry)
        {
            entry.OfferExpired(now, policy);
            entryId = entry.Id;
        }

        var next = await offers.OfferFreedSlotAsync(held.DoctorId, held.Time, cancellationToken, exclude: entryId);

        // Параллельно пациент мог принять предложение: тогда xmin отклонит сохранение — это нормально.
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? true : WaitlistOfferService.SaveError(saved.Error, next);
    }
}

internal sealed class ExpireStaleWaitlistEntriesHandler(IWaitlistRepository waitlist, IClock clock, ClinicPolicy policy)
    : ICommandHandler<ExpireStaleWaitlistEntries, int>
{
    public async Task<Result<int>> Handle(ExpireStaleWaitlistEntries command, CancellationToken cancellationToken) =>
        await waitlist.ExpireStaleAsync(clock.GetCurrentInstant().InZone(policy.TimeZone).Date, cancellationToken);
}

internal static class WaitlistOffers
{
    /// <summary>Удержание пациента и заявка, по которой оно предложено. Чужое — «не найдено».</summary>
    public static async Task<Result<(Appointment Appointment, WaitlistEntry Entry)>> FindOwnAsync(
        IAppointmentRepository appointments,
        IWaitlistRepository waitlist,
        AppointmentId appointmentId,
        PatientId patientId,
        CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetByIdAsync(appointmentId, cancellationToken);
        if (appointment is null || appointment.PatientId != patientId || appointment.WaitlistEntryId is not { } entryId)
        {
            return ApplicationErrors.NotFound("offer", appointmentId);
        }

        var entry = await waitlist.GetByIdAsync(entryId, cancellationToken);
        return entry is null ? ApplicationErrors.NotFound("offer", appointmentId) : (appointment, entry);
    }
}
