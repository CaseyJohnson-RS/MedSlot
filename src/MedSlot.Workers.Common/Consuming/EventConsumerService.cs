using System.Text.Json;
using Confluent.Kafka;
using MedSlot.Contracts;
using MedSlot.Workers.Common.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;

namespace MedSlot.Workers.Common.Consuming;

public enum ProcessingOutcome
{
    Processed,
    Duplicate,
    Skipped,
    DeadLettered,
}

/// <summary>
/// Цикл консьюмера (ADR 0010):
/// <list type="number">
/// <item>сообщения партиции обрабатываются по одному, по порядку;</item>
/// <item>обработка и отметка в inbox — одна транзакция; уже обработанное событие пропускается;</item>
/// <item>offset коммитится только после обработки (at-least-once);</item>
/// <item>после <see cref="ConsumerOptions.MaxAttempts"/> неудачных попыток сообщение уходит в DLT,
/// и консьюмер идёт дальше — одно «ядовитое» сообщение не останавливает партицию.</item>
/// </list>
/// Если не удалось даже отправить в DLT, исключение останавливает воркер без commit: после перезапуска
/// сообщение будет прочитано снова.
/// </summary>
public sealed partial class EventConsumerService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    ConsumerOptions options,
    DeadLetterPublisher deadLetters,
    IClock clock,
    ILogger<EventConsumerService<TDbContext>> logger) : BackgroundService
    where TDbContext : WorkerDbContext
{
    // Consume блокирует поток: цикл работает на отдельном потоке, не занимая пул.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(
            () => ConsumeLoopAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    /// <summary>Обрабатывает одно сообщение. Отдельный метод — чтобы проверять обработку без цикла.</summary>
    public async Task<ProcessingOutcome> ProcessAsync(ConsumeResult<string, string> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        EventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(message.Message.Value, EventEnvelope.SerializerOptions);
        }
        catch (JsonException ex)
        {
            await deadLetters.PublishAsync(message, options.ConsumerName, "invalid_envelope", ex, cancellationToken);
            DeadLettered(logger, message.TopicPartitionOffset.ToString(), "invalid_envelope");
            return ProcessingOutcome.DeadLettered;
        }

        if (envelope is null || envelope.EventId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.EventType))
        {
            await deadLetters.PublishAsync(message, options.ConsumerName, "invalid_envelope", null, cancellationToken);
            DeadLettered(logger, message.TopicPartitionOffset.ToString(), "invalid_envelope");
            return ProcessingOutcome.DeadLettered;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await HandleOnceAsync(envelope, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AttemptFailed(logger, envelope.EventType, envelope.EventId, attempt, ex);
                if (attempt >= options.MaxAttempts)
                {
                    await deadLetters.PublishAsync(message, options.ConsumerName, "handler_failed", ex, cancellationToken);
                    DeadLettered(logger, message.TopicPartitionOffset.ToString(), "handler_failed");
                    return ProcessingOutcome.DeadLettered;
                }

                await Task.Delay(options.RetryBackoff * Math.Pow(2, attempt - 1), cancellationToken);
            }
        }
    }

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,
            ClientId = options.ConsumerName,
            AutoOffsetReset = options.StartFromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
            EnableAutoCommit = false,
        })
            .SetPartitionsAssignedHandler((_, partitions) => PartitionsAssigned(logger, partitions))
            .SetPartitionsRevokedHandler((_, partitions) => PartitionsRevoked(logger, partitions))
            .SetErrorHandler((_, error) => KafkaError(logger, error.Reason))
            .Build();

        consumer.Subscribe(options.Topic);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? message;
                try
                {
                    message = consumer.Consume(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ConsumeException ex)
                {
                    KafkaError(logger, ex.Error.Reason);
                    continue;
                }

                if (message?.Message is null)
                {
                    continue;
                }

                await ProcessAsync(message, stoppingToken);

                // Commit после обработки: падение до этой строки даст повтор, который отсечёт inbox.
                consumer.Commit(message);
            }
        }
        finally
        {
            // Явный выход из группы: партиции сразу передаются другим экземплярам, без ожидания session timeout.
            consumer.Close();
        }
    }

    private async Task<ProcessingOutcome> HandleOnceAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var processors = scope.ServiceProvider.GetServices<IIntegrationEventProcessor>()
            .Where(h => h.CanHandle(envelope.EventType, envelope.SchemaVersion))
            .ToList();
        if (processors.Count == 0)
        {
            Skipped(logger, envelope.EventType, envelope.SchemaVersion);
            return ProcessingOutcome.Skipped;
        }

        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var alreadyProcessed = await db.ProcessedEvents.AnyAsync(
            e => e.Consumer == options.ConsumerName && e.EventId == envelope.EventId, cancellationToken);
        if (alreadyProcessed)
        {
            return ProcessingOutcome.Duplicate;
        }

        foreach (var processor in processors)
        {
            await processor.HandleAsync(envelope, cancellationToken);
        }

        db.ProcessedEvents.Add(new ProcessedEvent
        {
            Consumer = options.ConsumerName,
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            ProcessedAt = clock.GetCurrentInstant(),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: WorkerDbContext.InboxPrimaryKey,
        })
        {
            // То же событие параллельно обработал другой экземпляр (например, во время ребалансировки).
            return ProcessingOutcome.Duplicate;
        }

        await transaction.CommitAsync(cancellationToken);
        return ProcessingOutcome.Processed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Назначены партиции: {Partitions}")]
    private static partial void PartitionsAssigned(ILogger logger, List<TopicPartition> partitions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Отозваны партиции: {Partitions}")]
    private static partial void PartitionsRevoked(ILogger logger, List<TopicPartitionOffset> partitions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ошибка Kafka: {Reason}")]
    private static partial void KafkaError(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Событие {EventType} v{SchemaVersion} пропущено: нет обработчика")]
    private static partial void Skipped(ILogger logger, string eventType, int schemaVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не удалось обработать {EventType} {EventId}, попытка {Attempt}")]
    private static partial void AttemptFailed(ILogger logger, string eventType, Guid eventId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Сообщение {Position} отправлено в DLT: {Reason}")]
    private static partial void DeadLettered(ILogger logger, string position, string reason);
}
