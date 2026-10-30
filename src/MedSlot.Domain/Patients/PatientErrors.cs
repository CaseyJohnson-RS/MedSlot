using MedSlot.Domain.Common;

namespace MedSlot.Domain.Patients;

public static class PatientErrors
{
    public static readonly DomainError FullNameRequired =
        new("patient.full_name_required", "Не указано ФИО пациента.");

    public static readonly DomainError InvalidPhoneNumber =
        new("patient.invalid_phone_number", "Телефон должен быть в международном формате, например +7 900 123-45-67.");

    public static readonly DomainError InvalidEmail =
        new("patient.invalid_email", "Некорректный адрес электронной почты.");

    public static readonly DomainError CardAccessPurposeRequired =
        new("patient.card_access_purpose_required", "Укажите цель обращения к карточке пациента (до 200 символов).");

    public static DomainError TooManyActiveAppointments(int limit) =>
        new("patient.too_many_active_appointments", $"Нельзя иметь больше {limit} активных записей.");

    public static DomainError BookingBanned(Instant until) =>
        new("patient.booking_banned", $"Онлайн-запись недоступна до {until} из-за нарушений.");
}
