using MedSlot.Domain.Common;

namespace MedSlot.Domain.Patients;

/// <summary>Телефон в формате E.164: <c>+</c> и 10–15 цифр. Персональные данные.</summary>
public sealed record PhoneNumber
{
    private PhoneNumber(string value) => Value = value;

    public string Value { get; }

    public static Result<PhoneNumber> Create(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PatientErrors.InvalidPhoneNumber;
        }

        var trimmed = raw.Trim();
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        var allowedSeparators = trimmed.Skip(1).All(c => char.IsAsciiDigit(c) || c is ' ' or '-' or '(' or ')');

        if (!trimmed.StartsWith('+') || !allowedSeparators || digits.Length is < 10 or > 15)
        {
            return PatientErrors.InvalidPhoneNumber;
        }

        return new PhoneNumber($"+{digits}");
    }

    public override string ToString() => Value;
}
