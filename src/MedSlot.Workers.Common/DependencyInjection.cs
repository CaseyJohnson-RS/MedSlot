using MedSlot.Workers.Common.Consuming;
using MedSlot.Workers.Common.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;

namespace MedSlot.Workers.Common;

public static class DependencyInjection
{
    /// <summary>
    /// БД воркера и консьюмер топика событий. Обработчики (<see cref="IIntegrationEventProcessor"/>)
    /// воркер регистрирует сам как scoped.
    /// </summary>
    public static IServiceCollection AddEventConsumer<TDbContext>(
        this IServiceCollection services, string connectionString, ConsumerOptions options)
        where TDbContext : WorkerDbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.AddDbContext<TDbContext>(o => WorkerDatabase.Configure(o, connectionString));
        services.AddSingleton(options);
        services.AddSingleton<DeadLetterPublisher>();
        services.AddSingleton<EventConsumerService<TDbContext>>();
        services.AddHostedService(sp => sp.GetRequiredService<EventConsumerService<TDbContext>>());
        return services;
    }

    /// <summary>Применяет миграции БД воркера; создаёт базу, если её нет.</summary>
    public static async Task MigrateWorkerDatabaseAsync<TDbContext>(this IServiceProvider services, CancellationToken cancellationToken = default)
        where TDbContext : WorkerDbContext
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TDbContext>().Database.MigrateAsync(cancellationToken);
    }
}
