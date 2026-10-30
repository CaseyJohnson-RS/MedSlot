using System.Globalization;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;
using Npgsql;

namespace MedSlot.Workers.Notifications;

/// <summary>Контакты пациента. Персональные данные: не логируются и не сохраняются в БД воркера.</summary>
public sealed record PatientContacts(string FullName, string Phone, string? Email);

public interface IPatientDirectory
{
    Task<PatientContacts?> FindAsync(Guid patientId, CancellationToken cancellationToken);
}

/// <summary>
/// Читает контакты из таблицы <c>patients</c> основной БД (read-only).
/// Компромисс до аутентификации сервисов (фаза 7): затем — внутренний API с сервисным токеном (ADR 0010).
/// </summary>
internal sealed class ClinicPatientDirectory(NpgsqlDataSource clinicDatabase) : IPatientDirectory
{
    public async Task<PatientContacts?> FindAsync(Guid patientId, CancellationToken cancellationToken)
    {
        await using var command = clinicDatabase.CreateCommand("SELECT full_name, phone, email FROM patients WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = patientId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PatientContacts(reader.GetString(0), reader.GetString(1), await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2));
    }
}

public sealed record NotificationMessage(Guid NotificationId, Guid PatientId, string Subject, string Body);

public interface INotificationSender
{
    Task SendAsync(NotificationMessage message, PatientContacts recipient, CancellationToken cancellationToken);
}

/// <summary>Письмо через SMTP (Mailpit в docker-compose), если есть email; иначе — заглушка SMS в лог.</summary>
internal sealed partial class SmtpOrLogNotificationSender(NotificationsSettings settings, ILogger<SmtpOrLogNotificationSender> logger)
    : INotificationSender
{
    public async Task SendAsync(NotificationMessage message, PatientContacts recipient, CancellationToken cancellationToken)
    {
        if (recipient.Email is null)
        {
            // В лог — только идентификаторы, без телефона и текста.
            SmsStub(logger, message.NotificationId, message.PatientId);
            return;
        }

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort);
        using var mail = new MailMessage(settings.SmtpFrom, recipient.Email, message.Subject, message.Body);
        await client.SendMailAsync(mail, cancellationToken);
        EmailSent(logger, message.NotificationId, message.PatientId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMS (заглушка): уведомление {NotificationId} пациенту {PatientId}")]
    private static partial void SmsStub(ILogger logger, Guid notificationId, Guid patientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Письмо: уведомление {NotificationId} пациенту {PatientId}")]
    private static partial void EmailSent(ILogger logger, Guid notificationId, Guid patientId);
}

/// <summary>Тексты уведомлений. Время — в часовом поясе клиники.</summary>
internal static class NotificationTemplates
{
    private static readonly LocalDateTimePattern TimePattern =
        LocalDateTimePattern.Create("dd.MM.yyyy 'в' HH:mm", CultureInfo.GetCultureInfo("ru-RU"));

    public static NotificationMessage Render(Notification notification, PatientContacts recipient, DateTimeZone zone)
    {
        var startsAt = TimePattern.Format(notification.StartsAt.InZone(zone).LocalDateTime);
        var greeting = $"Здравствуйте, {recipient.FullName}!";

        var (subject, text) = notification.Kind switch
        {
            NotificationKind.BookingConfirmed => ("Вы записаны к врачу", $"Вы записаны на приём {startsAt}."),
            NotificationKind.Reminder => ("Напоминание о приёме", $"Напоминаем: завтра у вас приём, {startsAt}."),
            NotificationKind.Cancelled => ("Запись отменена", $"Ваша запись на {startsAt} отменена."),
            NotificationKind.WaitlistOffer => (
                "Освободилось время для записи",
                $"Освободилось время {startsAt}. Подтвердите запись до {TimePattern.Format(notification.RespondUntil!.Value.InZone(zone).LocalDateTime)}."),
            _ => throw new InvalidOperationException($"Нет шаблона для {notification.Kind}."),
        };

        return new NotificationMessage(notification.Id, notification.PatientId, subject, $"{greeting}\n\n{text}\n\nMedSlot");
    }
}
