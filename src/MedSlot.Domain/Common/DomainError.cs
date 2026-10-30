namespace MedSlot.Domain.Common;

/// <summary>
/// Бизнес-ошибка: нарушение правила домена, которое ожидаемо и сообщается клиенту.
/// Ошибки программиста (невалидные аргументы) — исключения, а не <see cref="DomainError"/>.
/// </summary>
/// <param name="Code">Стабильный машиночитаемый код, например <c>appointment.slot_in_past</c>.</param>
/// <param name="Message">Описание для человека.</param>
public sealed record DomainError(string Code, string Message)
{
    public static readonly DomainError None = new(string.Empty, string.Empty);
}
