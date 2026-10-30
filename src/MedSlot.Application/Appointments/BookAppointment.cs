using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Scheduling;

namespace MedSlot.Application.Appointments;

/// <summary>
/// Запись к врачу. <c>Source</c> — кто записывает: пациент сам или администратор (без бана и лимита).
/// </summary>
public sealed record BookAppointment(
    PatientId PatientId,
    DoctorId DoctorId,
    ServiceId ServiceId,
    Instant StartsAt,
    BookingSource Source) : ICommand<AppointmentId>;

internal sealed class BookAppointmentHandler(
    IDoctorRepository doctors,
    IServiceRepository services,
    IPatientRepository patients,
    IAppointmentRepository appointments,
    IUnitOfWork unitOfWork,
    IClock clock,
    ClinicPolicy policy) : ICommandHandler<BookAppointment, AppointmentId>
{
    public async Task<Result<AppointmentId>> Handle(BookAppointment command, CancellationToken cancellationToken)
    {
        var doctor = await doctors.GetByIdAsync(command.DoctorId, cancellationToken);
        if (doctor is null)
        {
            return ApplicationErrors.NotFound("doctor", command.DoctorId);
        }

        var service = await services.GetByIdAsync(command.ServiceId, cancellationToken);
        if (service is null)
        {
            return ApplicationErrors.NotFound("service", command.ServiceId);
        }

        var patient = await patients.GetByIdAsync(command.PatientId, cancellationToken);
        if (patient is null)
        {
            return ApplicationErrors.NotFound("patient", command.PatientId);
        }

        if (!doctor.Provides(service.Id))
        {
            return DoctorErrors.ServiceNotProvided;
        }

        if (!SlotCalculator.IsWithinSchedule(doctor, service, command.StartsAt, policy))
        {
            return AppointmentErrors.SlotNotInSchedule;
        }

        var now = clock.GetCurrentInstant();
        var history = new PatientBookingHistory(
            await appointments.GetViolationTimesAsync(
                patient.Id, now - policy.ViolationWindow - policy.BanDuration, cancellationToken),
            patient.BanLiftedAt,
            await appointments.CountUpcomingActiveAsync(patient.Id, now, cancellationToken));

        var eligibility = PatientBookingPolicy.CanBook(history, command.Source, now, policy);
        if (eligibility.IsFailure)
        {
            return eligibility.Error;
        }

        var appointment = Appointment.Book(
            AppointmentId.New(),
            doctor.Id,
            patient.Id,
            service.Id,
            TimeRange.FromStart(command.StartsAt, service.Duration),
            command.Source,
            now,
            policy);
        if (appointment.IsFailure)
        {
            return appointment.Error;
        }

        // Занятость врача и пациента проверяет exclusion constraint при сохранении (ADR 0005):
        // предварительная проверка здесь всё равно не защитила бы от гонки.
        appointments.Add(appointment.Value);
        var saved = await unitOfWork.SaveChangesAsync(cancellationToken);
        return saved.IsSuccess ? appointment.Value.Id : saved.Error;
    }
}
