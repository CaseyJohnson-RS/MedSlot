using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MedSlot.Application.Appointments;
using MedSlot.Contracts;
using MedSlot.Domain.Appointments;
using MedSlot.Integration.Tests.Infrastructure;
using MedSlot.Workers.Audit;
using MedSlot.Workers.Common;
using MedSlot.Workers.Common.Consuming;
using MedSlot.Workers.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using static MedSlot.Integration.Tests.Infrastructure.DatabaseFixture;

namespace MedSlot.Integration.Tests.Workers;

public sealed class WorkerTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly FakeSender _sender = new();
    private ServiceProvider _notifications = null!;
    private ServiceProvider _audit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EventConsumerService<NotificationsDbContext> NotificationsConsumer =>
        _notifications.GetRequiredService<EventConsumerService<NotificationsDbContext>>();

    private EventConsumerService<AuditDbContext> AuditConsumer =>
        _audit.GetRequiredService<EventConsumerService<AuditDbContext>>();

    public async ValueTask InitializeAsync()
    {
        fixture.Clock.Reset(BaseNow);

        // Своя группа на каждый тест: сквозной тест читает топик с начала, не мешая другим.
        var group = $"test-{Guid.NewGuid()}";
        _notifications = Build(services => services.AddNotificationsWorker(
            new NotificationsSettings
            {
                ConnectionString = fixture.DatabaseConnectionString("medslot_notifications_tests"),
                ClinicApiBaseUrl = new Uri("http://localhost/"),
                ClinicApiServiceKey = DatabaseFixture.NotificationsServiceKey,

                // Маленькая пачка: очередь в общей БД всегда больше неё, и обработка «до пустой очереди»
                // проверяется в каждом прогоне, а не только при неудачном порядке тестов.
                DispatchBatchSize = 5,
            },
            Consumer(NotificationsWorker.ConsumerName, group),
            // Запросы к API клиники уходят в тестовый сервер в памяти, без сети.
            clinicApi => clinicApi.ConfigurePrimaryHttpMessageHandler(() => fixture.Factory.Server.CreateHandler())));
        _audit = Build(services => services.AddAuditWorker(
            fixture.DatabaseConnectionString("medslot_audit_tests"),
            Consumer(AuditWorker.ConsumerName, group)));

        await _notifications.MigrateWorkerDatabaseAsync<NotificationsDbContext>(Ct);
        await _audit.MigrateWorkerDatabaseAsync<AuditDbContext>(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _notifications.DisposeAsync();
        await _audit.DisposeAsync();
    }

    private ServiceProvider Build(Action<IServiceCollection> addWorker)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<NodaTime.IClock>(fixture.Clock);
        addWorker(services);
        services.AddSingleton<INotificationSender>(_sender);
        return services.BuildServiceProvider();
    }

    private ConsumerOptions Consumer(string name, string group) => new()
    {
        BootstrapServers = fixture.KafkaBootstrapServers,
        GroupId = $"{group}-{name}",
        ConsumerName = name,
        MaxAttempts = 2,
        RetryBackoff = TimeSpan.FromMilliseconds(10),

        // С начала: сквозному тесту не нужно ждать назначения партиций до публикации.
        StartFromEarliest = true,
    };

    private static EventEnvelope Envelope<T>(string type, T payload, Guid doctorId, Guid aggregateId) => new(
        Guid.CreateVersion7(),
        type,
        1,
        BaseNow.ToDateTimeOffset(),
        aggregateId,
        doctorId,
        null,
        JsonSerializer.SerializeToElement(payload, EventEnvelope.SerializerOptions));

    private static EventEnvelope Booked(Guid appointmentId, Guid patientId, Instant startsAt, Guid? doctorId = null)
    {
        var doctor = doctorId ?? Guid.NewGuid();
        return Envelope(EventTypes.AppointmentBooked, new AppointmentBookedV1(
            appointmentId, doctor, patientId, Guid.NewGuid(), startsAt.ToDateTimeOffset(), (startsAt + Duration.FromMinutes(30)).ToDateTimeOffset(), "Patient"),
            doctor, appointmentId);
    }

    private static ConsumeResult<string, string> Message(string key, string value) => new()
    {
        Topic = Topics.Appointments,
        Partition = new Partition(0),
        Offset = new Confluent.Kafka.Offset(0),
        Message = new Message<string, string> { Key = key, Value = value, Headers = [] },
    };

    private static ConsumeResult<string, string> Message(EventEnvelope envelope) =>
        Message(envelope.DoctorId.ToString(), JsonSerializer.Serialize(envelope, EventEnvelope.SerializerOptions));

    private async Task<List<Notification>> NotificationsFor(Guid appointmentId)
    {
        await using var scope = _notifications.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Notifications
            .AsNoTracking()
            .Where(n => n.AppointmentId == appointmentId)
            .OrderBy(n => n.DueAt)
            .ToListAsync(Ct);
    }

    /// <summary>
    /// Прогоняет диспетчер до пустой очереди созревших уведомлений. БД уведомлений общая для тестов,
    /// и в одну пачку (50) уведомление этого теста может не попасть. Цикл конечен: каждое обработанное
    /// уведомление отправлено, помечено неудачным или перенесено в будущее.
    /// </summary>
    private async Task DispatchAllDueAsync()
    {
        var dispatcher = _notifications.GetRequiredService<NotificationDispatcher>();
        while (await dispatcher.DispatchDueAsync(Ct) > 0)
        {
        }
    }

    private async Task<int> AuditRecordsFor(Guid aggregateId)
    {
        await using var scope = _audit.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Records.CountAsync(r => r.AggregateId == aggregateId, Ct);
    }

    [Fact]
    public async Task Booking_schedules_confirmation_and_reminder()
    {
        var appointment = Guid.NewGuid();
        var startsAt = BaseNow + Duration.FromDays(7);

        (await NotificationsConsumer.ProcessAsync(Message(Booked(appointment, Guid.NewGuid(), startsAt)), Ct)).ShouldBe(ProcessingOutcome.Processed);

        var notifications = await NotificationsFor(appointment);
        notifications.Select(n => (n.Kind, n.DueAt)).ShouldBe(
        [
            (NotificationKind.BookingConfirmed, BaseNow),
            (NotificationKind.Reminder, startsAt - Duration.FromHours(24)),
        ]);
        notifications.ShouldAllBe(n => n.Status == NotificationStatus.Pending);
    }

    [Fact]
    public async Task Booking_less_than_a_day_ahead_gets_no_reminder()
    {
        var appointment = Guid.NewGuid();

        await NotificationsConsumer.ProcessAsync(Message(Booked(appointment, Guid.NewGuid(), BaseNow + Duration.FromHours(5))), Ct);

        (await NotificationsFor(appointment)).ShouldHaveSingleItem().Kind.ShouldBe(NotificationKind.BookingConfirmed);
    }

    [Fact]
    public async Task Redelivered_event_has_a_single_effect()
    {
        var appointment = Guid.NewGuid();
        var message = Message(Booked(appointment, Guid.NewGuid(), BaseNow + Duration.FromDays(7)));

        // Повтор доставки — например, воркер упал после обработки, но до commit offset.
        (await NotificationsConsumer.ProcessAsync(message, Ct)).ShouldBe(ProcessingOutcome.Processed);
        (await NotificationsConsumer.ProcessAsync(message, Ct)).ShouldBe(ProcessingOutcome.Duplicate);
        (await AuditConsumer.ProcessAsync(message, Ct)).ShouldBe(ProcessingOutcome.Processed);
        (await AuditConsumer.ProcessAsync(message, Ct)).ShouldBe(ProcessingOutcome.Duplicate);

        (await NotificationsFor(appointment)).Count.ShouldBe(2);
        (await AuditRecordsFor(appointment)).ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_cancels_pending_reminder()
    {
        var appointment = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var startsAt = BaseNow + Duration.FromDays(7);
        var booked = Booked(appointment, patient, startsAt);
        await NotificationsConsumer.ProcessAsync(Message(booked), Ct);

        var cancelled = Envelope(EventTypes.AppointmentCancelled, new AppointmentCancelledV1(
            appointment, booked.DoctorId, patient, startsAt.ToDateTimeOffset(), "Patient", false, null), booked.DoctorId, appointment);
        (await NotificationsConsumer.ProcessAsync(Message(cancelled), Ct)).ShouldBe(ProcessingOutcome.Processed);

        var notifications = await NotificationsFor(appointment);
        notifications.Single(n => n.Kind == NotificationKind.Reminder).Status.ShouldBe(NotificationStatus.Cancelled);
        notifications.Single(n => n.Kind == NotificationKind.Cancelled).Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public async Task Unknown_event_is_skipped_by_notifications_but_audited()
    {
        var aggregate = Guid.NewGuid();
        var message = Message(Envelope("SomethingNew", new { aggregate }, Guid.NewGuid(), aggregate));

        (await NotificationsConsumer.ProcessAsync(message, Ct)).ShouldBe(ProcessingOutcome.Skipped);
        (await AuditConsumer.ProcessAsync(message, Ct)).ShouldBe(ProcessingOutcome.Processed);
        (await AuditRecordsFor(aggregate)).ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_message_goes_to_dead_letter_topic()
    {
        var key = $"poison-{Guid.NewGuid()}";

        (await NotificationsConsumer.ProcessAsync(Message(key, "это не JSON"), Ct)).ShouldBe(ProcessingOutcome.DeadLettered);

        var dead = ConsumeDeadLetter(key);
        dead.Message.Value.ShouldBe("это не JSON");
        Header(dead, DeadLetterHeaders.Reason).ShouldBe("invalid_envelope");
        Header(dead, DeadLetterHeaders.Consumer).ShouldBe(NotificationsWorker.ConsumerName);
        Header(dead, DeadLetterHeaders.OriginalTopic).ShouldBe(Topics.Appointments);
    }

    [Fact]
    public async Task Processing_failure_is_retried_then_dead_lettered_without_effect()
    {
        var appointment = Guid.NewGuid();
        var doctor = Guid.NewGuid();

        // Корректный конверт с payload не той схемы: обработчик падает при каждой попытке.
        var broken = new EventEnvelope(
            Guid.CreateVersion7(), EventTypes.AppointmentBooked, 1, BaseNow.ToDateTimeOffset(), appointment, doctor, null,
            JsonSerializer.SerializeToElement("не payload", EventEnvelope.SerializerOptions));
        var message = Message(broken);

        (await NotificationsConsumer.ProcessAsync(message, Ct)).ShouldBe(ProcessingOutcome.DeadLettered);

        Header(ConsumeDeadLetter(doctor.ToString()), DeadLetterHeaders.Reason).ShouldBe("handler_failed");
        (await NotificationsFor(appointment)).ShouldBeEmpty();

        // Следующее сообщение обрабатывается как обычно: партиция не заблокирована.
        var next = Guid.NewGuid();
        (await NotificationsConsumer.ProcessAsync(Message(Booked(next, Guid.NewGuid(), BaseNow + Duration.FromDays(7), doctor)), Ct))
            .ShouldBe(ProcessingOutcome.Processed);
    }

    [Fact]
    public async Task Audit_log_is_append_only()
    {
        var appointment = Guid.NewGuid();
        await AuditConsumer.ProcessAsync(Message(Booked(appointment, Guid.NewGuid(), BaseNow + Duration.FromDays(7))), Ct);

        await using var connection = new NpgsqlConnection(fixture.DatabaseConnectionString("medslot_audit_tests"));
        await connection.OpenAsync(Ct);
        foreach (var sql in new[] { "UPDATE audit_log SET event_type = 'x'", "DELETE FROM audit_log", "TRUNCATE audit_log" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            error.MessageText.ShouldContain("только пополняется");
        }

        (await AuditRecordsFor(appointment)).ShouldBe(1);
    }

    [Fact]
    public async Task Dispatcher_sends_due_notifications_using_patient_contacts()
    {
        var clinic = await fixture.CreateClinicAsync();
        var appointment = Guid.NewGuid();
        var startsAt = BaseNow + Duration.FromDays(3);
        await NotificationsConsumer.ProcessAsync(Message(Booked(appointment, clinic.Patient.Value, startsAt)), Ct);
        // Сейчас созрело только подтверждение.
        await DispatchAllDueAsync();

        // В общей БД уведомлений могут ждать и уведомления других тестов — смотрим только своего пациента.
        var confirmation = _sender.Sent
            .Where(m => m.Message.PatientId == clinic.Patient.Value && m.Message.Subject == "Вы записаны к врачу")
            .ShouldHaveSingleItem();
        confirmation.Recipient.FullName.ShouldBe("Пациент 1");
        confirmation.Message.Body.ShouldContain("Здравствуйте, Пациент 1!");

        // За сутки до приёма уходит напоминание.
        fixture.Clock.Reset(startsAt - Duration.FromHours(24));
        await DispatchAllDueAsync();
        _sender.Sent.ShouldContain(m => m.Message.PatientId == clinic.Patient.Value && m.Message.Subject == "Напоминание о приёме");

        (await NotificationsFor(appointment)).ShouldAllBe(n => n.Status == NotificationStatus.Sent && n.SentAt != null);
    }

    [Fact]
    public async Task Notification_for_unknown_patient_fails_without_sending()
    {
        // Впереди в очереди — уведомления, созревшие раньше, больше одной пачки.
        // Так воспроизводится падение на CI: уведомление теста не попадало в первую пачку.
        fixture.Clock.Reset(BaseNow - Duration.FromHours(1));
        for (var i = 0; i < 6; i++)
        {
            await NotificationsConsumer.ProcessAsync(Message(Booked(Guid.NewGuid(), Guid.NewGuid(), BaseNow + Duration.FromHours(5))), Ct);
        }

        fixture.Clock.Reset(BaseNow);
        var appointment = Guid.NewGuid();
        await NotificationsConsumer.ProcessAsync(Message(Booked(appointment, Guid.NewGuid(), BaseNow + Duration.FromHours(5))), Ct);

        await DispatchAllDueAsync();

        var notification = (await NotificationsFor(appointment)).ShouldHaveSingleItem();
        notification.Status.ShouldBe(NotificationStatus.Failed);
        notification.LastError.ShouldBe("patient_not_found");
    }

    [Fact]
    public async Task Booking_flows_from_api_through_kafka_to_both_workers()
    {
        var clinic = await fixture.CreateClinicAsync();
        var id = (await fixture.SendAsync(new BookAppointment(
            clinic.Patient, clinic.Doctor, clinic.Long30, Msk(NextMonday, 10), BookingSource.Patient))).Value.Value;

        // Настоящие циклы консьюмеров: читают топик с начала в своих группах.
        await NotificationsConsumer.StartAsync(Ct);
        await AuditConsumer.StartAsync(Ct);
        try
        {
            await using (var relay = new MedSlot.Infrastructure.Outbox.OutboxRelay(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                fixture.Factory.Services.GetRequiredService<MedSlot.Infrastructure.Outbox.IEventPublisher>(),
                fixture.Factory.Services.GetRequiredService<MedSlot.Infrastructure.Outbox.OutboxOptions>(),
                fixture.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MedSlot.Infrastructure.Outbox.OutboxRelay>.Instance))
            {
                while ((await relay.RunOnceAsync(Ct)).Fetched > 0)
                {
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && ((await NotificationsFor(id)).Count < 2 || await AuditRecordsFor(id) < 1))
            {
                await Task.Delay(200, Ct);
            }

            (await NotificationsFor(id)).Select(n => n.Kind).ShouldBe([NotificationKind.BookingConfirmed, NotificationKind.Reminder]);
            (await AuditRecordsFor(id)).ShouldBe(1);
        }
        finally
        {
            await NotificationsConsumer.StopAsync(CancellationToken.None);
            await AuditConsumer.StopAsync(CancellationToken.None);
        }
    }

    private ConsumeResult<string, string> ConsumeDeadLetter(string key)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = fixture.KafkaBootstrapServers,
            GroupId = $"test-dlt-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(Topics.AppointmentsDeadLetter);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { } result && result.Message.Key == key)
            {
                return result;
            }
        }

        throw new ShouldAssertException($"Сообщение с ключом {key} не найдено в DLT.");
    }

    private static string Header(ConsumeResult<string, string> message, string name) =>
        Encoding.UTF8.GetString(message.Message.Headers.GetLastBytes(name));

    private sealed class FakeSender : INotificationSender
    {
        public List<(NotificationMessage Message, PatientContacts Recipient)> Sent { get; } = [];

        public Task SendAsync(NotificationMessage message, PatientContacts recipient, CancellationToken cancellationToken)
        {
            Sent.Add((message, recipient));
            return Task.CompletedTask;
        }
    }
}
