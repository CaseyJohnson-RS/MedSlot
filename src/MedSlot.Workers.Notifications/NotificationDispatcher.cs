using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace MedSlot.Workers.Notifications;

/// <summary>
/// Отправляет уведомления, у которых наступило время. Несколько экземпляров не мешают друг другу:
/// строки берутся через <c>FOR UPDATE SKIP LOCKED</c>. Отправка — at-least-once: если письмо ушло,
/// а коммит не прошёл, оно уйдёт ещё раз.
/// </summary>
public sealed partial class NotificationDispatcher(
    IServiceScopeFactory scopeFactory,
    IPatientDirectory patients,
    INotificationSender sender,
    NotificationsSettings settings,
    IClock clock,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    /// <summary>Отправляет пачку созревших уведомлений. Возвращает число отправленных.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = clock.GetCurrentInstant();
        var due = await db.Notifications
            .FromSql($"""
                SELECT * FROM notifications
                WHERE status = 'Pending' AND due_at <= {now}
                ORDER BY due_at
                LIMIT {settings.DispatchBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var notification in due)
        {
            var recipient = await patients.FindAsync(notification.PatientId, cancellationToken);
            if (recipient is null)
            {
                notification.Status = NotificationStatus.Failed;
                notification.LastError = "patient_not_found";
                continue;
            }

            try
            {
                await sender.SendAsync(NotificationTemplates.Render(notification, recipient, settings.ClinicTimeZone), recipient, cancellationToken);
                notification.Status = NotificationStatus.Sent;
                notification.SentAt = clock.GetCurrentInstant();
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notification.Attempts++;
                notification.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                if (notification.Attempts >= settings.MaxSendAttempts)
                {
                    notification.Status = NotificationStatus.Failed;
                }
                else
                {
                    // Повтор позже: 1, 2, 4, 8 минут.
                    notification.DueAt = now + Duration.FromMinutes(Math.Pow(2, notification.Attempts - 1));
                }

                SendFailed(logger, notification.Id, notification.Attempts, ex);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return sent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.DispatchInterval);
        do
        {
            try
            {
                // Пока очередь не пуста — без паузы.
                while (await DispatchDueAsync(stoppingToken) == settings.DispatchBatchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DispatchFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не удалось отправить уведомление {NotificationId}, попытка {Attempt}")]
    private static partial void SendFailed(ILogger logger, Guid notificationId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ошибка цикла отправки уведомлений")]
    private static partial void DispatchFailed(ILogger logger, Exception exception);
}
