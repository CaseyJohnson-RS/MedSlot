using MedSlot.Application.Abstractions;
using MedSlot.Domain.Clinic;
using MedSlot.Infrastructure.Idempotency;
using MedSlot.Infrastructure.Outbox;
using MedSlot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace MedSlot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        KafkaOptions kafka,
        OutboxOptions? outbox = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(kafka);

        outbox ??= new OutboxOptions();
        outbox.ConnectionString = connectionString;

        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(ClinicPolicy.Default);

        services.AddSingleton<DomainEventsToOutboxInterceptor>();
        services.AddDbContext<MedSlotDbContext>((sp, options) =>
        {
            ConfigureDbContext(options, connectionString);
            options.AddInterceptors(sp.GetRequiredService<DomainEventsToOutboxInterceptor>());
        });
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDoctorRepository, DoctorRepository>();
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<IPatientRepository, PatientRepository>();
        services.AddScoped<IAppointmentRepository, AppointmentRepository>();

        services.AddScoped<IdempotencyStore>();
        services.AddScoped<IIdempotencyStore>(sp => sp.GetRequiredService<IdempotencyStore>());

        services.AddSingleton(kafka);
        services.AddSingleton(outbox);
        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        services.AddSingleton<OutboxRelay>();
        if (outbox.RelayEnabled)
        {
            services.AddHostedService<OutboxRelayService>();
        }

        services.AddHealthChecks()
            .AddDbContextCheck<MedSlotDbContext>("postgres", tags: ["ready"])
            .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql
                .UseNodaTime()
                // Повтор при временных сбоях: обрыв соединения, редкий deadlock. Массовые deadlock
                // при параллельной записи на один слот предотвращают блокировки в UnitOfWork (ADR 0005).
                .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))
            .UseSnakeCaseNamingConvention();
}
