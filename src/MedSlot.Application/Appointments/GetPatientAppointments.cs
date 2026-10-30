using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;

namespace MedSlot.Application.Appointments;

public sealed record AppointmentDto(
    AppointmentId Id,
    DoctorId DoctorId,
    PatientId PatientId,
    ServiceId ServiceId,
    Instant StartsAt,
    Instant EndsAt,
    AppointmentStatus Status)
{
    internal static AppointmentDto From(Appointment a) =>
        new(a.Id, a.DoctorId, a.PatientId, a.ServiceId, a.Time.Start, a.Time.End, a.Status);
}

public sealed record GetAppointment(AppointmentId AppointmentId) : IQuery<AppointmentDto>;

public sealed record GetPatientAppointments(PatientId PatientId) : IQuery<IReadOnlyList<AppointmentDto>>;

internal sealed class GetAppointmentHandler(IAppointmentRepository appointments)
    : IQueryHandler<GetAppointment, AppointmentDto>
{
    public async Task<Result<AppointmentDto>> Handle(GetAppointment query, CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetByIdAsync(query.AppointmentId, cancellationToken);
        return appointment is null
            ? ApplicationErrors.NotFound("appointment", query.AppointmentId)
            : AppointmentDto.From(appointment);
    }
}

internal sealed class GetPatientAppointmentsHandler(IAppointmentRepository appointments)
    : IQueryHandler<GetPatientAppointments, IReadOnlyList<AppointmentDto>>
{
    public async Task<Result<IReadOnlyList<AppointmentDto>>> Handle(
        GetPatientAppointments query, CancellationToken cancellationToken)
    {
        var list = await appointments.GetByPatientAsync(query.PatientId, cancellationToken);
        return Result.Success<IReadOnlyList<AppointmentDto>>(list
            .OrderBy(a => a.Time.Start)
            .Select(AppointmentDto.From)
            .ToList());
    }
}
