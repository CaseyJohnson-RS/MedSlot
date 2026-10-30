using System.Globalization;
using System.Text;
using Confluent.Kafka;
using MedSlot.Contracts;

namespace MedSlot.Workers.Common.Consuming;

/// <summary>Заголовки, которые консьюмер добавляет к сообщению в DLT.</summary>
public static class DeadLetterHeaders
{
    public const string Consumer = "dlt-consumer";
    public const string Reason = "dlt-reason";
    public const string Error = "dlt-error";
    public const string OriginalTopic = "dlt-original-topic";
    public const string OriginalPartition = "dlt-original-partition";
    public const string OriginalOffset = "dlt-original-offset";
}

/// <summary>
/// Отправляет сообщение, которое не удалось обработать, в <see cref="Topics.AppointmentsDeadLetter"/>:
/// исходные ключ, значение и заголовки плюс причина. Партиция не блокируется, сообщение не теряется.
/// </summary>
public sealed class DeadLetterPublisher : IDisposable
{
    private readonly IProducer<string, string> _producer;

    public DeadLetterPublisher(ConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = $"{options.ConsumerName}-dlt",
            EnableIdempotence = true,
            Acks = Acks.All,
        }).Build();
    }

    public async Task PublishAsync(
        ConsumeResult<string, string> source, string consumer, string reason, Exception? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var headers = new Headers();
        foreach (var header in source.Message.Headers ?? [])
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        Add(headers, DeadLetterHeaders.Consumer, consumer);
        Add(headers, DeadLetterHeaders.Reason, reason);
        Add(headers, DeadLetterHeaders.OriginalTopic, source.Topic);
        Add(headers, DeadLetterHeaders.OriginalPartition, source.Partition.Value.ToString(CultureInfo.InvariantCulture));
        Add(headers, DeadLetterHeaders.OriginalOffset, source.Offset.Value.ToString(CultureInfo.InvariantCulture));
        if (error is not null)
        {
            var message = $"{error.GetType().Name}: {error.Message}";
            Add(headers, DeadLetterHeaders.Error, message.Length > 1000 ? message[..1000] : message);
        }

        await _producer.ProduceAsync(
            Topics.AppointmentsDeadLetter,
            new Message<string, string> { Key = source.Message.Key, Value = source.Message.Value, Headers = headers },
            cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    private static void Add(Headers headers, string key, string value) => headers.Add(key, Encoding.UTF8.GetBytes(value));
}
