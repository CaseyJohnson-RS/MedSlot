using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MedSlot.Infrastructure.Persistence.Converters;

internal sealed class DoctorIdConverter() : ValueConverter<DoctorId, Guid>(id => id.Value, value => new DoctorId(value));

internal sealed class PatientIdConverter() : ValueConverter<PatientId, Guid>(id => id.Value, value => new PatientId(value));

internal sealed class ServiceIdConverter() : ValueConverter<ServiceId, Guid>(id => id.Value, value => new ServiceId(value));

internal sealed class AppointmentIdConverter()
    : ValueConverter<AppointmentId, Guid>(id => id.Value, value => new AppointmentId(value));

internal sealed class WaitlistEntryIdConverter()
    : ValueConverter<WaitlistEntryId, Guid>(id => id.Value, value => new WaitlistEntryId(value));

internal sealed class UserIdConverter() : ValueConverter<UserId, Guid>(id => id.Value, value => new UserId(value));

internal sealed class CardAccessIdConverter()
    : ValueConverter<CardAccessId, Guid>(id => id.Value, value => new CardAccessId(value));

internal sealed class RefreshTokenIdConverter()
    : ValueConverter<RefreshTokenId, Guid>(id => id.Value, value => new RefreshTokenId(value));
