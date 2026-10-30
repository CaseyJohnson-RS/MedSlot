namespace MedSlot.Application.Abstractions;

/// <summary>Сохранённый ответ на запрос с ключом идемпотентности.</summary>
public sealed record IdempotentResponse(int StatusCode, string? ContentType, string? Location, string Body);

/// <summary>Ключ идемпотентности, уже известный системе.</summary>
/// <param name="RequestHash">Хэш запроса, с которым ключ использован впервые.</param>
/// <param name="Response">Сохранённый ответ; <c>null</c>, пока операция не завершена.</param>
public sealed record StoredIdempotencyKey(string RequestHash, IdempotentResponse? Response);

/// <summary>Хранилище ключей идемпотентности (ADR 0007).</summary>
public interface IIdempotencyStore
{
    /// <summary>Ищет действующий ключ. Ключи старше срока хранения считаются отсутствующими.</summary>
    Task<StoredIdempotencyKey?> FindAsync(string scope, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Занимает ключ. Строка не сохраняется сразу: она попадёт в БД тем же <see cref="IUnitOfWork.SaveChangesAsync"/>,
    /// что и изменения бизнес-операции, — одной транзакцией. Если операция ничего не сохранила, ключа в БД нет.
    /// </summary>
    void Begin(string scope, string key, string requestHash);

    /// <summary>Сохраняет ответ для ключа, занятого через <see cref="Begin"/>.</summary>
    Task CompleteAsync(string scope, string key, IdempotentResponse response, CancellationToken cancellationToken);
}
