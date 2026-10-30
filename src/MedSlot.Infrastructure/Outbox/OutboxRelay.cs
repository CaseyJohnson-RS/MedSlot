using System.Data;
using MedSlot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;

namespace MedSlot.Infrastructure.Outbox;

internal sealed record OutboxRelayResult(bool IsLeader, int Fetched, int Published, int Failed)
{
    public static readonly OutboxRelayResult NotLeader = new(false, 0, 0, 0);
}

/// <summary>
/// Публикует события из outbox в Kafka (ADR 0008, 0009).
/// <list type="bullet">
/// <item>Активен один экземпляр на всю систему: session-level advisory lock на отдельном соединении.</item>
/// <item>События публикуются строго по возрастанию id, по одному, с ожиданием подтверждения брокера.</item>
/// <item>Если событие врача не опубликовалось, следующие события этого врача в итерации пропускаются:
/// иначе они ушли бы в Kafka раньше него и порядок нарушился бы. События других врачей идут дальше.</item>
/// <item>Доставка at-least-once: падение между публикацией и отметкой даст повтор, консьюмеры идемпотентны.</item>
/// </list>
/// </summary>
internal sealed partial class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    OutboxOptions options,
    IClock clock,
    ILogger<OutboxRelay> logger) : IAsyncDisposable
{
    /// <summary>Ключ advisory-блокировки лидерства relay.</summary>
    internal const long LeaderLockKey = 0x4D65_6453_6C6F_7401;

    private NpgsqlConnection? _leaderConnection;

    public async Task<OutboxRelayResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureLeaderAsync(cancellationToken))
        {
            return OutboxRelayResult.NotLeader;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<MedSlotDbContext>().Set<OutboxMessage>();

        var batch = await outbox
            .AsNoTracking()
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        var blockedKeys = new HashSet<string>();
        int published = 0, failed = 0;
        foreach (var message in batch)
        {
            if (blockedKeys.Contains(message.PartitionKey))
            {
                continue;
            }

            try
            {
                await publisher.PublishAsync(message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                blockedKeys.Add(message.PartitionKey);
                failed++;
                PublishFailed(logger, message.Id, message.EventType, ex);

                var error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                await outbox
                    .Where(m => m.Id == message.Id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(m => m.Attempts, m => m.Attempts + 1).SetProperty(m => m.LastError, error),
                        cancellationToken);
                continue;
            }

            var now = clock.GetCurrentInstant();
            await outbox
                .Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAt, now), cancellationToken);
            published++;
        }

        return new OutboxRelayResult(true, batch.Count, published, failed);
    }

    /// <summary>Удаляет события, опубликованные раньше срока хранения. Выполняет только лидер.</summary>
    public async Task<int> DeletePublishedAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureLeaderAsync(cancellationToken))
        {
            return 0;
        }

        var threshold = clock.GetCurrentInstant() - Duration.FromTimeSpan(options.PublishedRetention);
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MedSlotDbContext>().Set<OutboxMessage>()
            .Where(m => m.PublishedAt != null && m.PublishedAt < threshold)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Закрытие соединения снимает session-level блокировку: лидерство переходит другому экземпляру.
        if (_leaderConnection is not null)
        {
            await _leaderConnection.DisposeAsync();
            _leaderConnection = null;
        }
    }

    private async Task<bool> EnsureLeaderAsync(CancellationToken cancellationToken)
    {
        if (_leaderConnection is not null)
        {
            if (await IsAliveAsync(_leaderConnection, cancellationToken))
            {
                return true;
            }

            // Соединение потеряно — вместе с ним и блокировка. Пытаемся взять заново.
            LostLeadership(logger);
            await DisposeAsync();
        }

        // Без пула: закрытие соединения должно завершать сессию в Postgres и тем самым снимать блокировку.
        // Соединение из пула при закрытии остаётся открытым (сброс сессии — лишь при следующем использовании),
        // и блокировка висела бы, не давая лидерству перейти к другому экземпляру.
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", LeaderLockKey);

            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                _leaderConnection = connection;
                BecameLeader(logger);
                return true;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return false;
    }

    private static async Task<bool> IsAliveAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            return false;
        }

        try
        {
            await using var ping = new NpgsqlCommand("SELECT 1", connection);
            await ping.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay стал активным")]
    private static partial void BecameLeader(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay потерял соединение с блокировкой лидерства")]
    private static partial void LostLeadership(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не удалось опубликовать событие {OutboxId} ({EventType})")]
    private static partial void PublishFailed(ILogger logger, long outboxId, string eventType, Exception exception);
}
