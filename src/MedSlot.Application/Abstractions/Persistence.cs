using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using MedSlot.Domain.Waitlist;

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

    void Add(Patient patient);
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

    /// <summary>Был ли пациент записан к врачу хотя бы раз — «свой пациент» для доступа к карточке.</summary>
    Task<bool> DoctorHasPatientAsync(DoctorId doctorId, PatientId patientId, CancellationToken cancellationToken);

    /// <summary>Есть ли у пациента активная запись, пересекающаяся с интервалом.</summary>
    Task<bool> HasActiveOverlapAsync(PatientId patientId, TimeRange range, CancellationToken cancellationToken);

    /// <summary>Самое давно истёкшее удержание (Held с HoldExpiresAt ≤ now), если есть.</summary>
    Task<Appointment?> GetNextExpiredHoldAsync(Instant now, CancellationToken cancellationToken);
}

/// <summary>Журнал доступа к карточкам: только пополняется.</summary>
public interface IPatientCardAccessRepository
{
    void Add(PatientCardAccess access);

    Task<IReadOnlyList<PatientCardAccess>> GetForPatientAsync(PatientId patientId, CancellationToken cancellationToken);
}

public interface IWaitlistRepository
{
    Task<WaitlistEntry?> GetByIdAsync(WaitlistEntryId id, CancellationToken cancellationToken);

    void Add(WaitlistEntry entry);

    /// <summary>Очередь к врачу на дату: заявки Active, чей диапазон содержит день <paramref name="day"/>, по позиции в очереди.</summary>
    Task<IReadOnlyList<WaitlistEntry>> GetQueueAsync(DoctorId doctorId, LocalDate day, CancellationToken cancellationToken);

    /// <summary>Число открытых заявок пациента (Active, Offered).</summary>
    Task<int> CountOpenAsync(PatientId patientId, CancellationToken cancellationToken);

    Task<bool> HasOpenAsync(PatientId patientId, DoctorId doctorId, ServiceId serviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WaitlistEntry>> GetByPatientAsync(PatientId patientId, CancellationToken cancellationToken);

    /// <summary>Закрывает заявки Active, чей диапазон дат уже прошёл. Возвращает число закрытых.</summary>
    Task<int> ExpireStaleAsync(LocalDate today, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    /// <summary>
    /// Сохраняет изменения одной транзакцией. Конфликты, которые ловит БД (занятое время,
    /// параллельное изменение записи), возвращаются как ошибки, а не исключения.
    /// </summary>
    Task<Result> SaveChangesAsync(CancellationToken cancellationToken);
}
