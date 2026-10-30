using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;

namespace MedSlot.Application.Appointments;

public sealed record AppointmentDto(
    AppointmentId Id,
    DoctorId DoctorId,
    ServiceId ServiceId,
    Instant StartsAt,
    Instant EndsAt,
    AppointmentStatus Status);

public sealed record GetPatientAppointments(PatientId PatientId) : IQuery<IReadOnlyList<AppointmentDto>>;

internal sealed class GetPatientAppointmentsHandler(IAppointmentRepository appointments)
    : IQueryHandler<GetPatientAppointments, IReadOnlyList<AppointmentDto>>
{
    public async Task<Result<IReadOnlyList<AppointmentDto>>> Handle(
        GetPatientAppointments query, CancellationToken cancellationToken)
    {
        var list = await appointments.GetByPatientAsync(query.PatientId, cancellationToken);
        return Result.Success<IReadOnlyList<AppointmentDto>>(list
            .OrderBy(a => a.Time.Start)
            .Select(a => new AppointmentDto(a.Id, a.DoctorId, a.ServiceId, a.Time.Start, a.Time.End, a.Status))
            .ToList());
    }
}
