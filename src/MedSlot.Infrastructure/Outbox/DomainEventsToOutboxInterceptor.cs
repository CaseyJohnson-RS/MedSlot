using System.Diagnostics;
using MedSlot.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MedSlot.Infrastructure.Outbox;

/// <summary>
/// Перед сохранением превращает доменные события агрегатов в строки <c>outbox_messages</c>.
/// Они вставляются тем же SaveChanges — одной транзакцией с изменением агрегата (ADR 0008):
/// событие есть ⇔ изменение сохранено.
/// </summary>
internal sealed class DomainEventsToOutboxInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        MoveEventsToOutbox(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        MoveEventsToOutbox(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void MoveEventsToOutbox(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var aggregates = context.ChangeTracker.Entries<IAggregateRoot>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();

        // Связь события с трассировкой запроса: консьюмер продолжит тот же trace (фаза 8).
        var traceParent = Activity.Current?.Id;

        foreach (var aggregate in aggregates)
        {
            context.Set<OutboxMessage>().AddRange(
                aggregate.DomainEvents.Select(e => IntegrationEventMapper.ToOutboxMessage(e, traceParent)));

            // При повторе SaveChanges (временный сбой) строки outbox уже в контексте — события не дублируются.
            aggregate.ClearDomainEvents();
        }
    }
}
