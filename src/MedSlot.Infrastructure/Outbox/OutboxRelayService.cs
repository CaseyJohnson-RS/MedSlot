using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedSlot.Infrastructure.Outbox;

/// <summary>Фоновый цикл relay: опрос outbox, экспоненциальная пауза при ошибках, периодическая очистка.</summary>
internal sealed partial class OutboxRelayService(
    OutboxRelay relay,
    OutboxOptions options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        var nextCleanup = timeProvider.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var result = await relay.RunOnceAsync(stoppingToken);

                if (result.IsLeader && timeProvider.GetUtcNow() >= nextCleanup)
                {
                    await relay.DeletePublishedAsync(stoppingToken);
                    nextCleanup = timeProvider.GetUtcNow() + CleanupInterval;
                }

                consecutiveFailures = result.Failed > 0 ? consecutiveFailures + 1 : 0;
                delay = consecutiveFailures > 0 ? Backoff(consecutiveFailures)
                    : result.Fetched == options.BatchSize ? TimeSpan.Zero // в outbox ещё есть события — без паузы
                    : options.PollInterval;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Например, недоступна БД. Не роняем процесс: пробуем снова после паузы.
                consecutiveFailures++;
                IterationFailed(logger, ex);
                delay = Backoff(consecutiveFailures);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await relay.DisposeAsync();
    }

    private TimeSpan Backoff(int failures)
    {
        var exponential = options.PollInterval * Math.Pow(2, Math.Min(failures, 10));
        return exponential < options.MaxBackoff ? exponential : options.MaxBackoff;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Итерация outbox relay завершилась ошибкой")]
    private static partial void IterationFailed(ILogger logger, Exception exception);
}
