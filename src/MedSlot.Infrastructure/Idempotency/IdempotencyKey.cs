using NodaTime;

namespace MedSlot.Infrastructure.Idempotency;

/// <summary>Строка таблицы <c>idempotency_keys</c>. Не доменная сущность: деталь протокола API.</summary>
internal sealed class IdempotencyKey
{
    public required string Scope { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 метода, пути и тела запроса, hex.</summary>
    public required string RequestHash { get; init; }

    public required Instant CreatedAt { get; init; }

    public Instant? CompletedAt { get; set; }

    public int? ResponseStatus { get; set; }

    public string? ResponseContentType { get; set; }

    public string? ResponseLocation { get; set; }

    public string? ResponseBody { get; set; }
}
