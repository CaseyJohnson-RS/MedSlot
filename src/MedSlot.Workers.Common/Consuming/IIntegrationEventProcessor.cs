using MedSlot.Contracts;

namespace MedSlot.Workers.Common.Consuming;

/// <summary>
/// Обработчик интеграционных событий воркера. Регистрируется как scoped.
/// Изменения делает через scoped DbContext воркера, но не сохраняет их сам:
/// сохраняет консьюмер — одной транзакцией с отметкой в inbox.
/// Внешних побочных эффектов (письмо, HTTP) здесь быть не должно: при повторе они повторятся.
/// Такие эффекты записываются в таблицу и выполняются отдельно (см. уведомления).
/// </summary>
public interface IIntegrationEventProcessor
{
    /// <summary>
    /// Поддерживает ли обработчик событие. Неподдерживаемые типы и версии схем пропускаются:
    /// так старый воркер переживает появление новых событий (forward compatibility).
    /// </summary>
    bool CanHandle(string eventType, int schemaVersion);

    Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
