using MedSlot.Domain.Common;

namespace MedSlot.Domain.Services;

public static class ServiceErrors
{
    public static readonly DomainError NameRequired =
        new("service.name_required", "Не указано название услуги.");

    public static readonly DomainError DurationMustBePositive =
        new("service.duration_must_be_positive", "Длительность услуги должна быть больше нуля.");

    public static DomainError DurationNotMultipleOfGrid(Duration duration, Duration gridStep) =>
        new(
            "service.duration_not_multiple_of_grid",
            $"Длительность услуги ({duration.TotalMinutes} мин) должна быть кратна шагу сетки ({gridStep.TotalMinutes} мин).");
}
