using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MedSlot.Application.Appointments;
using MedSlot.Contracts;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Infrastructure.Outbox;
using MedSlot.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Outbox;

public class OutboxTests
{
    private static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(20);

    private readonly DatabaseFixture _fixture;

    public OutboxTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.Clock.Reset(BaseNow);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OutboxRelay CreateRelay(IEventPublisher? publisher = null) => new(
        _fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        publisher ?? _fixture.Factory.Services.GetRequiredService<IEventPublisher>(),
        _fixture.Factory.Services.GetRequiredService<OutboxOptions>(),
        _fixture.Clock,
        NullLogger<OutboxRelay>.Instance);

    /// <summary>Публикует всё, что накопили другие тесты, чтобы тест работал только со своими событиями.</summary>
    private async Task DrainAsync()
    {
        await using var relay = CreateRelay();
        while ((await relay.RunOnceAsync(Ct)).Fetched > 0)
        {
        }
    }

    private Task<Result<AppointmentId>> Book(TestClinic clinic, int hour, int patient = 0) =>
        _fixture.SendAsync(new BookAppointment(
            clinic.Patients[patient], clinic.Doctor, clinic.Long30, Msk(NextMonday, hour), BookingSource.Patient));

    private Task<List<OutboxMessage>> OutboxOf(TestClinic clinic) =>
        _fixture.WithDbAsync(db => db.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => m.PartitionKey == clinic.Doctor.Value.ToString())
            .OrderBy(m => m.Id)
            .ToListAsync(Ct));

    /// <summary>Читает топик с начала, пока не наберёт <paramref name="count"/> событий врача.</summary>
    private List<ConsumeResult<string, string>> ConsumeForDoctor(DoctorId doctor, int count)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _fixture.KafkaBootstrapServers,
            GroupId = $"test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(Topics.Appointments);

        var key = doctor.Value.ToString();
        var found = new List<ConsumeResult<string, string>>();
        var deadline = DateTime.UtcNow + ConsumeTimeout;
        while (found.Count < count && DateTime.UtcNow < deadline)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { } result && result.Message.Key == key)
            {
                found.Add(result);
            }
        }

        consumer.Close();
        return found;
    }

    private static EventEnvelope Envelope(ConsumeResult<string, string> result) =>
        JsonSerializer.Deserialize<EventEnvelope>(result.Message.Value, EventEnvelope.SerializerOptions)!;

    [Fact]
    public async Task Booking_event_is_published_with_doctor_key_and_envelope()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var appointmentId = (await Book(clinic, 10)).Value;

        // Событие записано в outbox той же транзакцией и ещё не опубликовано.
        var pending = (await OutboxOf(clinic)).ShouldHaveSingleItem();
        pending.EventType.ShouldBe(EventTypes.AppointmentBooked);
        pending.PublishedAt.ShouldBeNull();

        await DrainAsync();

        var message = ConsumeForDoctor(clinic.Doctor, 1).ShouldHaveSingleItem();
        var headers = message.Message.Headers;
        Encoding.UTF8.GetString(headers.GetLastBytes(EventHeaders.EventType)).ShouldBe(EventTypes.AppointmentBooked);
        Encoding.UTF8.GetString(headers.GetLastBytes(EventHeaders.EventId)).ShouldBe(pending.EventId.ToString());

        var envelope = Envelope(message);
        envelope.EventType.ShouldBe(EventTypes.AppointmentBooked);
        envelope.SchemaVersion.ShouldBe(1);
        envelope.AggregateId.ShouldBe(appointmentId.Value);
        envelope.DoctorId.ShouldBe(clinic.Doctor.Value);

        var payload = envelope.PayloadAs<AppointmentBookedV1>();
        payload.ShouldBe(new AppointmentBookedV1(
            appointmentId.Value,
            clinic.Doctor.Value,
            clinic.Patient.Value,
            clinic.Long30.Value,
            Msk(NextMonday, 10).ToDateTimeOffset(),
            Msk(NextMonday, 10, 30).ToDateTimeOffset(),
            "Patient"));

        // Без персональных данных: ни ФИО, ни телефона пациента в сообщении нет.
        message.Message.Value.ShouldNotContain("Пациент");
        message.Message.Value.ShouldNotContain("+7900");

        (await OutboxOf(clinic)).ShouldHaveSingleItem().PublishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Failed_operation_writes_no_events()
    {
        var clinic = await _fixture.CreateClinicAsync(patients: 2);
        (await Book(clinic, 11)).IsSuccess.ShouldBeTrue();

        (await Book(clinic, 11, patient: 1)).Error.ShouldBe(AppointmentErrors.SlotAlreadyTaken);

        // Транзакция откатилась вместе со строкой outbox: событие только об успешной записи.
        var events = await OutboxOf(clinic);
        events.ShouldHaveSingleItem().Envelope.ShouldContain(clinic.Patients[0].Value.ToString());
    }

    [Fact]
    public async Task Events_of_one_doctor_are_published_in_order()
    {
        var clinic = await _fixture.CreateClinicAsync();
        var first = (await Book(clinic, 12)).Value;
        (await _fixture.SendAsync(new CancelAppointmentByPatient(first, clinic.Patient))).IsSuccess.ShouldBeTrue();
        var second = (await Book(clinic, 14)).Value;

        await DrainAsync();
        var messages = ConsumeForDoctor(clinic.Doctor, 3);

        messages.Select(m => Envelope(m).EventType).ShouldBe(
            [EventTypes.AppointmentBooked, EventTypes.AppointmentCancelled, EventTypes.AppointmentBooked]);
        messages.Select(m => Envelope(m).AggregateId).ShouldBe([first.Value, first.Value, second.Value]);
        messages.Select(m => m.Partition.Value).Distinct().ShouldHaveSingleItem();
        messages.Select(m => m.Offset.Value).ShouldBeInOrder();
    }

    [Fact]
    public async Task Events_wait_in_outbox_while_kafka_is_unavailable()
    {
        await DrainAsync();
        var clinic = await _fixture.CreateClinicAsync();
        var first = (await Book(clinic, 15)).Value;
        (await _fixture.SendAsync(new CancelAppointmentByPatient(first, clinic.Patient))).IsSuccess.ShouldBeTrue();

        // Брокер недоступен: событие не публикуется, остаётся в outbox с отметкой о попытке.
        using (var unavailable = new KafkaEventPublisher(new KafkaOptions { BootstrapServers = "localhost:1", MessageTimeoutMs = 2000 }))
        {
            await using var relay = CreateRelay(unavailable);
            var result = await relay.RunOnceAsync(Ct);

            result.Published.ShouldBe(0);
            result.Failed.ShouldBe(1); // второе событие этого врача даже не пытались отправить
        }

        var stuck = await OutboxOf(clinic);
        stuck.ShouldAllBe(m => m.PublishedAt == null);
        stuck[0].Attempts.ShouldBe(1);
        stuck[0].LastError.ShouldNotBeNullOrEmpty();
        stuck[1].Attempts.ShouldBe(0);

        // Брокер снова доступен: оба события доставлены в исходном порядке.
        await DrainAsync();
        ConsumeForDoctor(clinic.Doctor, 2)
            .Select(m => Envelope(m).EventType)
            .ShouldBe([EventTypes.AppointmentBooked, EventTypes.AppointmentCancelled]);
    }

    [Fact]
    public async Task Failure_blocks_only_later_events_of_the_same_doctor()
    {
        await DrainAsync();
        var failing = await _fixture.CreateClinicAsync();
        var healthy = await _fixture.CreateClinicAsync();
        var id = (await Book(failing, 16)).Value;
        (await _fixture.SendAsync(new CancelAppointmentByPatient(id, failing.Patient))).IsSuccess.ShouldBeTrue();
        (await Book(healthy, 16)).IsSuccess.ShouldBeTrue();

        var publisher = new FailingForKeyPublisher(failing.Doctor.Value.ToString());
        await using (var relay = CreateRelay(publisher))
        {
            var result = await relay.RunOnceAsync(Ct);
            result.Published.ShouldBe(1);
            result.Failed.ShouldBe(1);
        }

        // Первое событие проблемного врача пытались отправить, второе — нет: иначе оно обогнало бы первое.
        publisher.Attempted.Count(key => key == failing.Doctor.Value.ToString()).ShouldBe(1);
        publisher.Attempted.ShouldContain(healthy.Doctor.Value.ToString());
        (await OutboxOf(failing)).ShouldAllBe(m => m.PublishedAt == null);
        (await OutboxOf(healthy)).ShouldAllBe(m => m.PublishedAt != null);

        await DrainAsync();
    }

    [Fact]
    public async Task Only_one_relay_is_active_at_a_time()
    {
        await using var first = CreateRelay();
        await using var second = CreateRelay();

        (await first.RunOnceAsync(Ct)).IsLeader.ShouldBeTrue();
        (await second.RunOnceAsync(Ct)).ShouldBe(OutboxRelayResult.NotLeader);

        // Первый остановился — блокировка снята вместе с его соединением, лидером становится второй.
        await first.DisposeAsync();
        (await second.RunOnceAsync(Ct)).IsLeader.ShouldBeTrue();
    }

    [Fact]
    public async Task Published_events_are_deleted_after_retention()
    {
        var clinic = await _fixture.CreateClinicAsync();
        (await Book(clinic, 17)).IsSuccess.ShouldBeTrue();
        await DrainAsync();

        _fixture.Clock.Reset(BaseNow + Duration.FromDays(8));
        await using (var relay = CreateRelay())
        {
            (await relay.DeletePublishedAsync(Ct)).ShouldBeGreaterThan(0);
        }

        (await OutboxOf(clinic)).ShouldBeEmpty();
    }

    private sealed class FailingForKeyPublisher(string failingKey) : IEventPublisher
    {
        public List<string> Attempted { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Attempted.Add(message.PartitionKey);
            return message.PartitionKey == failingKey
                ? Task.FromException(new InvalidOperationException("Брокер отклонил сообщение"))
                : Task.CompletedTask;
        }
    }
}
