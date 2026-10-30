using MedSlot.Workers.Audit;
using MedSlot.Workers.Common;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Audit")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Audit.");
var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Не задан адрес Kafka: Kafka:BootstrapServers.");

builder.Services.AddAuditWorker(connectionString, AuditWorker.DefaultConsumer(bootstrapServers));

var host = builder.Build();

// У воркера своя база: миграции применяются при старте, база создаётся при первом запуске.
await host.Services.MigrateWorkerDatabaseAsync<AuditDbContext>();
await host.RunAsync();
