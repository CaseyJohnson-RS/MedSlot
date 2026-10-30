using MedSlot.Domain.Common;

namespace MedSlot.Domain.Patients;

/// <summary>
/// Адрес электронной почты. Проверка намеренно простая: точно проверить адрес можно только письмом.
/// Персональные данные.
/// </summary>
public sealed record Email
{
    private Email(string value) => Value = value;

    public string Value { get; }

    public static Result<Email> Create(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PatientErrors.InvalidEmail;
        }

        var trimmed = raw.Trim();
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        var valid = at > 0
            && at == trimmed.LastIndexOf('@')
            && trimmed.IndexOf('.', at) > at + 1
            && !trimmed.EndsWith('.')
            && !trimmed.Any(char.IsWhiteSpace);

        return valid ? new Email(trimmed.ToLowerInvariant()) : PatientErrors.InvalidEmail;
    }

    public override string ToString() => Value;
}
