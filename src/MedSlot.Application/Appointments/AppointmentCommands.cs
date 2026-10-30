using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;

namespace MedSlot.Application.Appointments;

// Команды над существующей записью: загрузить, вызвать доменный метод, сохранить.
// Проверку, что врач отмечает только свои приёмы, добавит авторизация (фаза 7).

/// <summary>Пациент отменяет свою запись.</summary>
public sealed record CancelAppointmentByPatient(AppointmentId AppointmentId, PatientId PatientId) : ICommand<Unit>;

/// <summary>Врач или администратор отменяет запись. Не считается нарушением пациента.</summary>
public sealed record CancelAppointmentByClinic(AppointmentId AppointmentId, string ReasonCode) : ICommand<Unit>;

public sealed record CompleteAppointment(AppointmentId AppointmentId) : ICommand<Unit>;

public sealed record MarkAppointmentNoShow(AppointmentId AppointmentId) : ICommand<Unit>;

internal sealed class CancelAppointmentByPatientHandler(
    IAppointmentRepository appointments, IUnitOfWork unitOfWork, IClock clock, ClinicPolicy policy)
    : ICommandHandler<CancelAppointmentByPatient, Unit>
{
    public async Task<Result<Unit>> Handle(CancelAppointmentByPatient command, CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetByIdAsync(command.AppointmentId, cancellationToken);

        // Чужая запись неотличима от несуществующей: не раскрываем, что такой id есть.
        if (appointment is null || appointment.PatientId != command.PatientId)
        {
            return ApplicationErrors.NotFound("appointment", command.AppointmentId);
        }

        return await AppointmentChange.SaveAsync(
            appointment.CancelByPatient(clock.GetCurrentInstant(), policy), unitOfWork, cancellationToken);
    }
}

internal sealed class CancelAppointmentByClinicHandler(
    IAppointmentRepository appointments, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<CancelAppointmentByClinic, Unit>
{
    public Task<Result<Unit>> Handle(CancelAppointmentByClinic command, CancellationToken cancellationToken) =>
        AppointmentChange.ApplyAsync(
            appointments, unitOfWork, command.AppointmentId,
            a => a.CancelByClinic(clock.GetCurrentInstant(), command.ReasonCode), cancellationToken);
}

internal sealed class CompleteAppointmentHandler(
    IAppointmentRepository appointments, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<CompleteAppointment, Unit>
{
    public Task<Result<Unit>> Handle(CompleteAppointment command, CancellationToken cancellationToken) =>
        AppointmentChange.ApplyAsync(
            appointments, unitOfWork, command.AppointmentId,
            a => a.Complete(clock.GetCurrentInstant()), cancellationToken);
}

internal sealed class MarkAppointmentNoShowHandler(
    IAppointmentRepository appointments, IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<MarkAppointmentNoShow, Unit>
{
    public Task<Result<Unit>> Handle(MarkAppointmentNoShow command, CancellationToken cancellationToken) =>
        AppointmentChange.ApplyAsync(
            appointments, unitOfWork, command.AppointmentId,
            a => a.MarkNoShow(clock.GetCurrentInstant()), cancellationToken);
}

internal static class AppointmentChange
{
    public static async Task<Result<Unit>> ApplyAsync(
        IAppointmentRepository appointments,
        IUnitOfWork unitOfWork,
        AppointmentId id,
        Func<Appointment, Result> change,
        CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetByIdAsync(id, cancellationToken);
        if (appointment is null)
        {
            return ApplicationErrors.NotFound("appointment", id);
        }

        return await SaveAsync(change(appointment), unitOfWork, cancellationToken);
    }

    public static async Task<Result<Unit>> SaveAsync(Result change, IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        if (change.IsFailure)
        {
            return change.Error;
        }

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}
