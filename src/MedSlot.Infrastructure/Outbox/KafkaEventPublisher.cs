using System.Globalization;
using System.Text;
using Confluent.Kafka;
using MedSlot.Contracts;

namespace MedSlot.Infrastructure.Outbox;

/// <summary>Публикация одного события. Отдельный интерфейс — чтобы проверять relay без Kafka.</summary>
internal interface IEventPublisher
{
    /// <summary>Возвращает управление, когда брокер подтвердил запись (acks=all); иначе бросает исключение.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

internal sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaEventPublisher(KafkaOptions options)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = "medslot-outbox-relay",

            // Идемпотентный продюсер: повторная отправка после сетевого сбоя не создаёт дубль в партиции
            // и не меняет порядок. Требует acks=all.
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = options.MessageTimeoutMs,
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var headers = new Headers
        {
            { EventHeaders.EventId, Encoding.UTF8.GetBytes(message.EventId.ToString()) },
            { EventHeaders.EventType, Encoding.UTF8.GetBytes(message.EventType) },
            { EventHeaders.SchemaVersion, Encoding.UTF8.GetBytes(1.ToString(CultureInfo.InvariantCulture)) },
        };
        if (message.TraceParent is not null)
        {
            headers.Add(EventHeaders.TraceParent, Encoding.UTF8.GetBytes(message.TraceParent));
        }

        await _producer.ProduceAsync(
            Topics.Appointments,
            new Message<string, string> { Key = message.PartitionKey, Value = message.Envelope, Headers = headers },
            cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
