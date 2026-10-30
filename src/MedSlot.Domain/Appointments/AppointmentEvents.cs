using MedSlot.Domain.Common;

namespace MedSlot.Domain.Appointments;

// События записи (domain.md, §9). Только идентификаторы и время — без персональных данных.
// DoctorId есть в каждом событии: это ключ партиционирования в Kafka.

public sealed record AppointmentBooked(
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    ServiceId ServiceId,
    Instant StartsAt,
    Instant EndsAt,
    BookingSource BookedBy,
    Instant OccurredAt) : IDomainEvent;

public sealed record AppointmentCancelled(
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    Instant StartsAt,
    CancellationInitiator InitiatedBy,
    bool IsLate,
    string? ReasonCode,
    Instant OccurredAt) : IDomainEvent;

public sealed record AppointmentCompleted(
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    Instant OccurredAt) : IDomainEvent;

public sealed record AppointmentNoShow(
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    bool Auto,
    Instant OccurredAt) : IDomainEvent;

public sealed record WaitlistSlotOffered(
    WaitlistEntryId WaitlistEntryId,
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    Instant StartsAt,
    Instant EndsAt,
    Instant ExpiresAt,
    Instant OccurredAt) : IDomainEvent;

public sealed record WaitlistOfferAccepted(
    WaitlistEntryId WaitlistEntryId,
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    Instant OccurredAt) : IDomainEvent;

public sealed record WaitlistOfferDeclined(
    WaitlistEntryId WaitlistEntryId,
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    Instant OccurredAt) : IDomainEvent;

public sealed record WaitlistOfferExpired(
    WaitlistEntryId WaitlistEntryId,
    AppointmentId AppointmentId,
    DoctorId DoctorId,
    PatientId PatientId,
    Instant OccurredAt) : IDomainEvent;
