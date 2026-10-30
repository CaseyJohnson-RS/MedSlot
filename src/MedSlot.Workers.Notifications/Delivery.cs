using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace MedSlot.Workers.Notifications;

/// <summary>Контакты пациента. Персональные данные: не логируются и не сохраняются в БД воркера.</summary>
public sealed record PatientContacts(string FullName, string Phone, string? Email);

public interface IPatientDirectory
{
    Task<PatientContacts?> FindAsync(Guid patientId, CancellationToken cancellationToken);
}

/// <summary>
/// Контакты из внутреннего API клиники по ключу сервиса (ADR 0012). Каждое обращение API записывает
/// в журнал доступа к карточке; схему БД клиники воркер не знает.
/// </summary>
internal sealed class ApiPatientDirectory(HttpClient http) : IPatientDirectory
{
    public const string ServiceKeyHeader = "X-Service-Key";

    public async Task<PatientContacts?> FindAsync(Guid patientId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            new Uri($"internal/v1/patients/{patientId}/contacts?purpose=notification", UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var contacts = await response.Content.ReadFromJsonAsync<ContactsResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Пустой ответ внутреннего API контактов.");
        return new PatientContacts(contacts.FullName, contacts.Phone, contacts.Email);
    }

    private sealed record ContactsResponse(string FullName, string Phone, string? Email);
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
