using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedSlot.Infrastructure.Idempotency;

/// <summary>Раз в час удаляет ключи идемпотентности старше срока хранения.</summary>
public sealed partial class IdempotencyCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<IdempotencyCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<IdempotencyStore>().DeleteExpiredAsync(stoppingToken);
                if (deleted > 0)
                {
                    Deleted(logger, deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Очистка не критична: попробуем на следующем тике.
                Failed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Удалено устаревших ключей идемпотентности: {Count}")]
    private static partial void Deleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не удалось удалить устаревшие ключи идемпотентности")]
    private static partial void Failed(ILogger logger, Exception exception);
}
