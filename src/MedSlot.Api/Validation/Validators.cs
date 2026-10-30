using FluentValidation;
using MedSlot.Api.Contracts;
using MedSlot.Application.Scheduling;

namespace MedSlot.Api.Validation;

// Проверка формы запроса. Бизнес-правила (свободен ли слот, нет ли бана) проверяет домен.

internal sealed class BookAppointmentRequestValidator : AbstractValidator<BookAppointmentRequest>
{
    public BookAppointmentRequestValidator()
    {
        RuleFor(r => r.PatientId).NotEmpty();
        RuleFor(r => r.DoctorId).NotEmpty();
        RuleFor(r => r.ServiceId).NotEmpty();
        RuleFor(r => r.StartsAt).NotEmpty().WithMessage("Укажите время начала приёма.");
    }
}

internal sealed class CancelByPatientRequestValidator : AbstractValidator<CancelByPatientRequest>
{
    public CancelByPatientRequestValidator() => RuleFor(r => r.PatientId).NotEmpty();
}

internal sealed class CancelByClinicRequestValidator : AbstractValidator<CancelByClinicRequest>
{
    public CancelByClinicRequestValidator() =>
        RuleFor(r => r.ReasonCode)
            .NotEmpty()
            .MaximumLength(100)
            .Matches("^[a-z][a-z0-9_]*$")
            .WithMessage("Код причины: латиница в нижнем регистре, цифры и подчёркивания, например doctor_sick.");
}

internal sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Phone).NotEmpty();
        RuleFor(r => r.Email).NotEmpty().MaximumLength(254);
        RuleFor(r => r.Password)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128)
            .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithMessage("Пароль: не короче 8 символов, хотя бы одна буква и одна цифра.");
    }
}

internal sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty();
        RuleFor(r => r.Password).NotEmpty();
    }
}

internal sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator() => RuleFor(r => r.RefreshToken).NotEmpty().MaximumLength(100);
}

internal sealed class JoinWaitlistRequestValidator : AbstractValidator<JoinWaitlistRequest>
{
    // Не дальше горизонта записи (60 дней).
    private const int MaxDays = 60;

    public JoinWaitlistRequestValidator()
    {
        RuleFor(r => r.PatientId).NotEmpty();
        RuleFor(r => r.DoctorId).NotEmpty();
        RuleFor(r => r.ServiceId).NotEmpty();
        RuleFor(r => r.To)
            .GreaterThanOrEqualTo(r => r.From)
            .WithMessage("Дата окончания не может быть раньше даты начала.");
        RuleFor(r => r)
            .Must(r => r.To.DayNumber - r.From.DayNumber < MaxDays)
            .WithName("To")
            .WithMessage($"Диапазон не длиннее {MaxDays} дней.");
    }
}

internal sealed class WaitlistActionRequestValidator : AbstractValidator<WaitlistActionRequest>
{
    public WaitlistActionRequestValidator() => RuleFor(r => r.PatientId).NotEmpty();
}

/// <summary>Параметры запроса свободных слотов.</summary>
/// <param name="DoctorId">Врач (из пути).</param>
/// <param name="ServiceId">Услуга: её длительность определяет длину слота.</param>
/// <param name="From">Первая дата по календарю клиники.</param>
/// <param name="To">Последняя дата включительно.</param>
public sealed record SlotsQuery(Guid DoctorId, Guid ServiceId, DateOnly From, DateOnly To);

internal sealed class SlotsQueryValidator : AbstractValidator<SlotsQuery>
{
    public SlotsQueryValidator()
    {
        RuleFor(q => q.ServiceId).NotEmpty();
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From)
            .WithMessage("Дата окончания не может быть раньше даты начала.");
        RuleFor(q => q)
            .Must(q => q.To.DayNumber - q.From.DayNumber < GetAvailableSlots.MaxDays)
            .WithName("To")
            .WithMessage($"Диапазон не длиннее {GetAvailableSlots.MaxDays} дней.");
    }
}
