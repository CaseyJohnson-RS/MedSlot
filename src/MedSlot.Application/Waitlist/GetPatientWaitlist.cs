using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Waitlist;

namespace MedSlot.Application.Waitlist;

public sealed record WaitlistOfferDto(AppointmentId AppointmentId, Instant StartsAt, Instant EndsAt, Instant RespondUntil);

public sealed record WaitlistEntryDto(
    WaitlistEntryId Id,
    DoctorId DoctorId,
    ServiceId ServiceId,
    LocalDate From,
    LocalDate To,
    WaitlistEntryStatus Status,
    Instant QueuedAt,
    WaitlistOfferDto? Offer);

public sealed record GetPatientWaitlist(PatientId PatientId) : IQuery<IReadOnlyList<WaitlistEntryDto>>;

internal sealed class GetPatientWaitlistHandler(IWaitlistRepository waitlist, IAppointmentRepository appointments)
    : IQueryHandler<GetPatientWaitlist, IReadOnlyList<WaitlistEntryDto>>
{
    public async Task<Result<IReadOnlyList<WaitlistEntryDto>>> Handle(GetPatientWaitlist query, CancellationToken cancellationToken)
    {
        var result = new List<WaitlistEntryDto>();
        foreach (var entry in (await waitlist.GetByPatientAsync(query.PatientId, cancellationToken)).OrderBy(e => e.CreatedAt))
        {
            WaitlistOfferDto? offer = null;
            if (entry.CurrentOfferId is { } offerId
                && await appointments.GetByIdAsync(offerId, cancellationToken) is { Status: AppointmentStatus.Held, HoldExpiresAt: { } until } held)
            {
                offer = new WaitlistOfferDto(held.Id, held.Time.Start, held.Time.End, until);
            }

            result.Add(new WaitlistEntryDto(
                entry.Id, entry.DoctorId, entry.ServiceId, entry.From, entry.To, entry.Status, entry.QueuedAt, offer));
        }

        return Result.Success<IReadOnlyList<WaitlistEntryDto>>(result);
    }
}
