using MedSlot.Application.Waitlist;
using MedSlot.Domain.Waitlist;

namespace MedSlot.Api.Contracts;

/// <summary>Встать в лист ожидания: даты по календарю клиники, включительно. Пациент — из токена.</summary>
public sealed record JoinWaitlistRequest(Guid DoctorId, Guid ServiceId, DateOnly From, DateOnly To);

public sealed record WaitlistEntryCreatedResponse(Guid Id);

/// <summary>Предложенное время: ответить нужно до <c>RespondUntil</c>.</summary>
public sealed record WaitlistOfferResponse(Guid AppointmentId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset RespondUntil);

public sealed record WaitlistEntryResponse(
    Guid Id,
    Guid DoctorId,
    Guid ServiceId,
    DateOnly From,
    DateOnly To,
    WaitlistEntryStatus Status,
    DateTimeOffset QueuedAt,
    WaitlistOfferResponse? Offer)
{
    internal static WaitlistEntryResponse Create(WaitlistEntryDto dto) => new(
        dto.Id.Value,
        dto.DoctorId.Value,
        dto.ServiceId.Value,
        dto.From.ToDateOnly(),
        dto.To.ToDateOnly(),
        dto.Status,
        dto.QueuedAt.ToDateTimeOffset(),
        dto.Offer is { } offer
            ? new WaitlistOfferResponse(
                offer.AppointmentId.Value,
                offer.StartsAt.ToDateTimeOffset(),
                offer.EndsAt.ToDateTimeOffset(),
                offer.RespondUntil.ToDateTimeOffset())
            : null);
}
