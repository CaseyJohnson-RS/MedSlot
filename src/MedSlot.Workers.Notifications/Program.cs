using MedSlot.Workers.Common;
using MedSlot.Workers.Notifications;

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

var settings = new NotificationsSettings
{
    ConnectionString = config.GetConnectionString("Notifications")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Notifications."),
    ClinicApiBaseUrl = new Uri(config["ClinicApi:BaseUrl"]
        ?? throw new InvalidOperationException("Не задан адрес API клиники: ClinicApi:BaseUrl.")),
    ClinicApiServiceKey = config["ClinicApi:ServiceKey"]
        ?? throw new InvalidOperationException("Не задан ключ сервиса: ClinicApi:ServiceKey."),
    SmtpHost = config["Smtp:Host"] ?? "localhost",
    SmtpPort = config.GetValue("Smtp:Port", 1025),
};
var bootstrapServers = config["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Не задан адрес Kafka: Kafka:BootstrapServers.");

builder.Services.AddNotificationsWorker(settings, NotificationsWorker.DefaultConsumer(bootstrapServers));

var host = builder.Build();

// У воркера своя база: миграции применяются при старте, база создаётся при первом запуске.
await host.Services.MigrateWorkerDatabaseAsync<NotificationsDbContext>();
await host.RunAsync();
