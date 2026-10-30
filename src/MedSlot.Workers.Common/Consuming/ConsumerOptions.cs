using MedSlot.Contracts;

namespace MedSlot.Workers.Common.Consuming;

public sealed class ConsumerOptions
{
    public required string BootstrapServers { get; init; }

    /// <summary>Группа консьюмеров Kafka: экземпляры одного воркера делят партиции между собой.</summary>
    public required string GroupId { get; init; }

    /// <summary>Имя консьюмера в inbox.</summary>
    public required string ConsumerName { get; init; }

    public string Topic { get; init; } = Topics.Appointments;

    /// <summary>
    /// С какого места читать, если у группы ещё нет сохранённого offset (первый запуск воркера).
    /// Аудиту нужна вся история, а уведомления о давно прошедших событиях рассылать нельзя.
    /// </summary>
    public bool StartFromEarliest { get; init; } = true;

    /// <summary>Попыток обработать сообщение до отправки в DLT.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Пауза перед второй попыткой; дальше удваивается.</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(1);
}
