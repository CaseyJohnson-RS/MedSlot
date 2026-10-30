using MedSlot.Application.Abstractions;
using MedSlot.Application.Waitlist;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedSlot.Infrastructure.Waitlist;

/// <summary>
/// Снимает истёкшие предложения из листа ожидания (время отдаётся следующему в очереди)
/// и раз в час закрывает заявки с прошедшим диапазоном дат.
/// Каждое предложение — отдельная единица работы: конфликт с параллельным принятием одного
/// предложения (xmin) не мешает обработать остальные.
/// </summary>
public sealed partial class WaitlistExpiryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<WaitlistExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaleEntriesInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextStaleCheck = timeProvider.GetUtcNow();
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                while (await ExpireNextAsync(stoppingToken))
                {
                }

                if (timeProvider.GetUtcNow() >= nextStaleCheck)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider
                        .GetRequiredService<ICommandHandler<ExpireStaleWaitlistEntries, int>>()
                        .Handle(new ExpireStaleWaitlistEntries(), stoppingToken);
                    nextStaleCheck = timeProvider.GetUtcNow() + StaleEntriesInterval;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IterationFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <returns><c>true</c>, если предложение снято и стоит проверить следующее.</returns>
    private async Task<bool> ExpireNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<ICommandHandler<ExpireNextWaitlistOffer, bool>>()
            .Handle(new ExpireNextWaitlistOffer(), cancellationToken);

        if (result.IsFailure)
        {
            // Например, пациент принял предложение в ту же секунду. Повторим на следующем тике.
            ExpireRejected(logger, result.Error.Code);
            return false;
        }

        return result.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Снятие истёкшего предложения отложено: {ErrorCode}")]
    private static partial void ExpireRejected(ILogger logger, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ошибка обработки истёкших предложений листа ожидания")]
    private static partial void IterationFailed(ILogger logger, Exception exception);
}
