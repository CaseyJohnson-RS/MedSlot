using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;

namespace MedSlot.Application.Abstractions;

public interface IDoctorRepository
{
    Task<Doctor?> GetByIdAsync(DoctorId id, CancellationToken cancellationToken);
}

public interface IServiceRepository
{
    Task<Service?> GetByIdAsync(ServiceId id, CancellationToken cancellationToken);
}

public interface IPatientRepository
{
    Task<Patient?> GetByIdAsync(PatientId id, CancellationToken cancellationToken);
}

public interface IAppointmentRepository
{
    Task<Appointment?> GetByIdAsync(AppointmentId id, CancellationToken cancellationToken);

    void Add(Appointment appointment);

    /// <summary>Интервалы активных записей врача, пересекающиеся с окном.</summary>
    Task<IReadOnlyList<TimeRange>> GetBusyRangesAsync(DoctorId doctorId, TimeRange window, CancellationToken cancellationToken);

    /// <summary>Моменты нарушений пациента (LateCancelled, NoShow) после <paramref name="since"/>.</summary>
    Task<IReadOnlyList<Instant>> GetViolationTimesAsync(PatientId patientId, Instant since, CancellationToken cancellationToken);

    /// <summary>Число активных записей пациента, которые ещё не начались.</summary>
    Task<int> CountUpcomingActiveAsync(PatientId patientId, Instant now, CancellationToken cancellationToken);

    Task<IReadOnlyList<Appointment>> GetByPatientAsync(PatientId patientId, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    /// <summary>
    /// Сохраняет изменения одной транзакцией. Конфликты, которые ловит БД (занятое время,
    /// параллельное изменение записи), возвращаются как ошибки, а не исключения.
    /// </summary>
    Task<Result> SaveChangesAsync(CancellationToken cancellationToken);
}
