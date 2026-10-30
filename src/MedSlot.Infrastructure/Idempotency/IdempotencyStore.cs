using MedSlot.Application.Abstractions;
using MedSlot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace MedSlot.Infrastructure.Idempotency;

internal sealed class IdempotencyStore(MedSlotDbContext db, IClock clock) : IIdempotencyStore
{
    public async Task<StoredIdempotencyKey?> FindAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var record = await db.Set<IdempotencyKey>()
            .AsNoTracking()
            .SingleOrDefaultAsync(k => k.Scope == scope && k.Key == key, cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (record.CreatedAt < clock.GetCurrentInstant() - IdempotencyLimits.Retention)
        {
            // Просроченный ключ ещё не убрала фоновая очистка: освобождаем его для нового запроса.
            await db.Set<IdempotencyKey>()
                .Where(k => k.Scope == scope && k.Key == key)
                .ExecuteDeleteAsync(cancellationToken);
            return null;
        }

        var response = record.ResponseStatus is { } status
            ? new IdempotentResponse(status, record.ResponseContentType, record.ResponseLocation, record.ResponseBody ?? string.Empty)
            : null;
        return new StoredIdempotencyKey(record.RequestHash, response);
    }

    public void Begin(string scope, string key, string requestHash) =>
        db.Set<IdempotencyKey>().Add(new IdempotencyKey
        {
            Scope = scope,
            Key = key,
            RequestHash = requestHash,
            CreatedAt = clock.GetCurrentInstant(),
        });

    public async Task CompleteAsync(string scope, string key, IdempotentResponse response, CancellationToken cancellationToken)
    {
        // Строка отслеживается с момента Begin: после SaveChanges операции она Unchanged (будет UPDATE),
        // если операция ничего не сохраняла — всё ещё Added (будет INSERT уже с ответом).
        var record = db.ChangeTracker.Entries<IdempotencyKey>()
            .Select(e => e.Entity)
            .SingleOrDefault(k => k.Scope == scope && k.Key == key)
            ?? throw new InvalidOperationException("Ключ идемпотентности не был занят в этом запросе.");

        record.CompletedAt = clock.GetCurrentInstant();
        record.ResponseStatus = response.StatusCode;
        record.ResponseContentType = response.ContentType;
        record.ResponseLocation = response.Location;
        record.ResponseBody = response.Body;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Удаляет ключи старше срока хранения. Возвращает число удалённых.</summary>
    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetCurrentInstant() - IdempotencyLimits.Retention;
        return db.Set<IdempotencyKey>().Where(k => k.CreatedAt < threshold).ExecuteDeleteAsync(cancellationToken);
    }
}
