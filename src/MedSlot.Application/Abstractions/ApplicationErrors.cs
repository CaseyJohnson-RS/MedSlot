using MedSlot.Domain.Common;

namespace MedSlot.Application.Abstractions;

public static class ApplicationErrors
{
    public static readonly DomainError ConcurrentModification =
        new("common.concurrent_modification", "Данные изменились параллельно. Обновите их и повторите действие.");

    public static readonly DomainError IdempotencyKeyInUse =
        new("idempotency.request_in_progress", "Запрос с этим ключом идемпотентности уже выполняется.");

    public static DomainError NotFound(string entity, object id) =>
        new($"{entity}.not_found", $"Не найдено: {entity} {id}.");

    public static readonly DomainError DateRangeReversed =
        new("common.invalid_date_range", "Дата окончания не может быть раньше даты начала.");

    public static DomainError InvalidDateRange(int maxDays) =>
        new("common.invalid_date_range", $"Диапазон дат должен быть непустым и не длиннее {maxDays} дней.");
}
