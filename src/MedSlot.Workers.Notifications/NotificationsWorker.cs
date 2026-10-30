using MedSlot.Workers.Common;
using MedSlot.Workers.Common.Consuming;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace MedSlot.Workers.Notifications;

public sealed class NotificationsSettings
{
    public required string ConnectionString { get; init; }

    /// <summary>Адрес API клиники: контакты пациента берутся из его внутреннего эндпоинта (ADR 0012).</summary>
    public required Uri ClinicApiBaseUrl { get; init; }

    /// <summary>Ключ сервиса уведомлений для внутреннего API.</summary>
    public required string ClinicApiServiceKey { get; init; }

    public DateTimeZone ClinicTimeZone { get; init; } = DateTimeZoneProviders.Tzdb["Europe/Moscow"];

    public Duration ReminderLeadTime { get; init; } = Duration.FromHours(24);

    public TimeSpan DispatchInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int DispatchBatchSize { get; init; } = 50;

    public int MaxSendAttempts { get; init; } = 5;

    public string SmtpHost { get; init; } = "localhost";

    public int SmtpPort { get; init; } = 1025;

    public string SmtpFrom { get; init; } = "noreply@medslot.local";
}

public static class NotificationsWorker
{
    public const string ConsumerName = "notifications";

    public static ConsumerOptions DefaultConsumer(string bootstrapServers) => new()
    {
        BootstrapServers = bootstrapServers,
        GroupId = "medslot-notifications",
        ConsumerName = ConsumerName,

        // Первый запуск не должен разослать уведомления о событиях, случившихся до него.
        StartFromEarliest = false,
    };

    /// <param name="configureClinicApi">Доп. настройка HTTP-клиента к API клиники (в тестах — обработчик тестового сервера).</param>
    public static IServiceCollection AddNotificationsWorker(
        this IServiceCollection services,
        NotificationsSettings settings,
        ConsumerOptions consumer,
        Action<IHttpClientBuilder>? configureClinicApi = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        services.AddEventConsumer<NotificationsDbContext>(settings.ConnectionString, consumer);
        services.AddScoped<IIntegrationEventProcessor, NotificationsProcessor>();

        services.AddSingleton(settings);
        var clinicApi = services.AddHttpClient<IPatientDirectory, ApiPatientDirectory>(client =>
        {
            client.BaseAddress = settings.ClinicApiBaseUrl;
            client.DefaultRequestHeaders.Add(ApiPatientDirectory.ServiceKeyHeader, settings.ClinicApiServiceKey);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        configureClinicApi?.Invoke(clinicApi);

        services.AddSingleton<INotificationSender, SmtpOrLogNotificationSender>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
        return services;
    }
}
