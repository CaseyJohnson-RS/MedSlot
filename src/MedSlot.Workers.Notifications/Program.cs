using MedSlot.Workers.Common;
using MedSlot.Workers.Notifications;

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

var settings = new NotificationsSettings
{
    ConnectionString = config.GetConnectionString("Notifications")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Notifications."),
    ClinicConnectionString = config.GetConnectionString("Clinic")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Clinic."),
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
