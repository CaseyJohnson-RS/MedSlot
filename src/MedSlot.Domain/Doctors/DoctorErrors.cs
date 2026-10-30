using MedSlot.Domain.Common;

namespace MedSlot.Domain.Doctors;

public static class DoctorErrors
{
    public static readonly DomainError FullNameRequired =
        new("doctor.full_name_required", "Не указано ФИО врача.");

    public static readonly DomainError SpecializationRequired =
        new("doctor.specialization_required", "Не указана специализация врача.");

    public static readonly DomainError CustomHoursEmpty =
        new("doctor.custom_hours_empty", "Для особых часов нужен хотя бы один рабочий интервал.");

    public static readonly DomainError ServiceNotProvided =
        new("doctor.service_not_provided", "Врач не оказывает эту услугу.");

    public static DomainError OverlappingWorkingHours(LocalTimeRange first, LocalTimeRange second) =>
        new("doctor.overlapping_working_hours", $"Рабочие интервалы пересекаются: {first} и {second}.");

    public static DomainError OverlappingScheduleOverride(DateInterval dates) =>
        new("doctor.overlapping_schedule_override", $"На даты {dates} уже есть исключение из расписания.");

    public static DomainError ScheduleOverrideNotFound(DateInterval dates) =>
        new("doctor.schedule_override_not_found", $"Исключение из расписания на даты {dates} не найдено.");
}
