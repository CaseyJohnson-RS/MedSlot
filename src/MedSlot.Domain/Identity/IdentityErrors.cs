using MedSlot.Domain.Common;

namespace MedSlot.Domain.Identity;

public static class IdentityErrors
{
    public static readonly DomainError EmailTaken =
        new("auth.email_taken", "Учётная запись с этим email уже существует.");

    /// <summary>Неверный логин или пароль — без уточнения, что именно: не подсказываем, какие email зарегистрированы.</summary>
    public static readonly DomainError InvalidCredentials =
        new("auth.invalid_credentials", "Неверный email или пароль.");

    public static readonly DomainError InvalidRefreshToken =
        new("auth.invalid_refresh_token", "Сессия истекла или недействительна. Войдите снова.");

    public static readonly DomainError RefreshTokenReused =
        new("auth.refresh_token_reused", "Токен уже использован. Все сессии этого входа завершены. Войдите снова.");

    public static readonly DomainError DoctorRequired =
        new("auth.doctor_required", "Для учётной записи врача нужно указать врача.");

    public static readonly DomainError AdminCannotBeDoctor =
        new("auth.admin_cannot_be_doctor", "Учётная запись администратора не привязывается к врачу.");

    public static readonly DomainError NotAStaffRole =
        new("auth.not_a_staff_role", "Сотрудник может быть только врачом или администратором.");
}
