namespace MedSlot.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    /// <summary>Запускать ли relay в этом процессе. В воркерах и тестах выключен.</summary>
    public bool RelayEnabled { get; set; } = true;

    /// <summary>Пауза между опросами, когда новых событий нет.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public int BatchSize { get; set; } = 100;

    /// <summary>Потолок паузы при повторяющихся ошибках публикации (экспоненциальный рост).</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько хранить опубликованные события.</summary>
    public TimeSpan PublishedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Подключение для блокировки лидерства; задаётся в AddInfrastructure.</summary>
    internal string ConnectionString { get; set; } = string.Empty;
}

public sealed class KafkaOptions
{
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// Сколько продюсер пытается доставить сообщение. По умолчанию в librdkafka — 5 минут:
    /// при недоступной Kafka relay завис бы на столько же. Короткий таймаут + повтор из outbox.
    /// </summary>
    public int MessageTimeoutMs { get; init; } = 10_000;

    public int Partitions { get; init; } = 6;

    public short ReplicationFactor { get; init; } = 1;
}
