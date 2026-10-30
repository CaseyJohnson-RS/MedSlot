using MedSlot.Domain.Common;

namespace MedSlot.Domain.Waitlist;

public static class WaitlistErrors
{
    public static readonly DomainError DatesInPast =
        new("waitlist.dates_in_past", "Диапазон дат должен начинаться не раньше сегодняшнего дня.");

    public static readonly DomainError DatesBeyondHorizon =
        new("waitlist.dates_beyond_horizon", "На эти даты запись ещё не открыта.");

    public static readonly DomainError DuplicateEntry =
        new("waitlist.duplicate_entry", "Вы уже в листе ожидания к этому врачу на эту услугу.");

    public static DomainError TooManyEntries(int limit) =>
        new("waitlist.too_many_entries", $"Нельзя иметь больше {limit} активных заявок в листе ожидания.");

    public static DomainError InvalidStatus(WaitlistEntryStatus current, string action) =>
        new("waitlist.invalid_status", $"Нельзя выполнить «{action}» для заявки в статусе {current}.");
}
