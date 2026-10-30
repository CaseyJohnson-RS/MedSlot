using Confluent.Kafka;
using Confluent.Kafka.Admin;
using MedSlot.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MedSlot.Infrastructure.Outbox;

public static class KafkaTopics
{
    /// <summary>
    /// Создаёт топики, если их нет. Автосоздание в брокере выключено, поэтому для локального стенда и тестов
    /// топики создаются явно; в продакшене — шагом развёртывания.
    /// </summary>
    public static async Task InitializeKafkaTopicsAsync(this IServiceProvider services)
    {
        var options = services.GetRequiredService<KafkaOptions>();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();

        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification
                {
                    Name = Topics.Appointments,
                    NumPartitions = options.Partitions,
                    ReplicationFactor = options.ReplicationFactor,
                },
                new TopicSpecification
                {
                    Name = Topics.AppointmentsDeadLetter,
                    NumPartitions = 1,
                    ReplicationFactor = options.ReplicationFactor,
                },
            ]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Топики уже есть.
        }
    }
}

/// <summary>Kafka доступна, если брокер отвечает на запрос метаданных.</summary>
internal sealed class KafkaHealthCheck(KafkaOptions options) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                try
                {
                    using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
                    var metadata = admin.GetMetadata(Timeout);
                    return metadata.Brokers.Count > 0
                        ? HealthCheckResult.Healthy()
                        : HealthCheckResult.Unhealthy("Нет доступных брокеров Kafka.");
                }
                catch (KafkaException ex)
                {
                    return HealthCheckResult.Unhealthy("Kafka недоступна.", ex);
                }
            },
            cancellationToken);
}
