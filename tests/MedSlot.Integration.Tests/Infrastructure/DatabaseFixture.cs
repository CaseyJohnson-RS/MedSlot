using System.Collections.Concurrent;
using MedSlot.Application.Abstractions;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using MedSlot.Infrastructure.Outbox;
using MedSlot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodaTime.Testing;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(MedSlot.Integration.Tests.Infrastructure.DatabaseFixture))]

namespace MedSlot.Integration.Tests.Infrastructure;

/// <summary>
/// Один Postgres, одна Kafka в контейнерах и одно приложение на все интеграционные тесты.
/// Outbox relay в приложении выключен: тесты запускают его итерации сами.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public static readonly DateTimeZone Moscow = DateTimeZoneProviders.Tzdb["Europe/Moscow"];

    /// <summary>Понедельник, 09:00 по Москве.</summary>
    public static readonly Instant BaseNow = Msk(new LocalDate(2026, 11, 2), 9);

    /// <summary>Ключ воркера уведомлений для внутреннего API.</summary>
    public const string NotificationsServiceKey = "test-notifications-service-key";

    /// <summary>Понедельник через неделю после <see cref="BaseNow"/>: в горизонте записи.</summary>
    public static readonly LocalDate NextMonday = new(2026, 11, 9);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.2").Build();

    // Учётная запись создаётся один раз на субъекта, даже при параллельных запросах из теста.
    private readonly ConcurrentDictionary<string, Lazy<Task<UserAccount>>> _accounts = new();

    private MedSlotApiFactory? _factory;

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    /// <summary>Основная БД (та, что у API).</summary>
    public string ClinicConnectionString => _postgres.GetConnectionString();

    /// <summary>Отдельная база на том же сервере — для воркеров.</summary>
    public string DatabaseConnectionString(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    public FakeClock Clock { get; } = new(BaseNow);

    public ClinicPolicy Policy { get; } = ClinicPolicy.Default;

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Фикстура не инициализирована.");

    public static Instant Msk(LocalDate date, int hour, int minute = 0) =>
        date.At(new LocalTime(hour, minute)).InZoneStrictly(Moscow).ToInstant();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());
        _factory = new MedSlotApiFactory(_postgres.GetConnectionString(), KafkaBootstrapServers, Clock);
        await _factory.Services.InitializeDatabaseAsync(seedDemoData: false);
        await _factory.Services.InitializeKafkaTopicsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask());
    }

    /// <summary>Выполняет команду в отдельном DI-scope — как отдельный HTTP-запрос.</summary>
    public async Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResponse));
        return await InvokeAsync<TResponse>(scope.ServiceProvider.GetRequiredService(handlerType), command);
    }

    public async Task<Result<TResponse>> QueryAsync<TResponse>(IQuery<TResponse> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResponse));
        return await InvokeAsync<TResponse>(scope.ServiceProvider.GetRequiredService(handlerType), query);
    }

    public async Task<T> WithDbAsync<T>(Func<MedSlotDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<MedSlotDbContext>());
    }

    /// <summary>Новый врач (будни 09:00–13:00 и 14:00–18:00), услуги на 30 и 15 минут и пациенты.</summary>
    public async Task<TestClinic> CreateClinicAsync(int patients = 1)
    {
        var long30 = Service.Create(ServiceId.New(), "Первичный", Duration.FromMinutes(30), Policy).Value;
        var short15 = Service.Create(ServiceId.New(), "Повторный", Duration.FromMinutes(15), Policy).Value;

        var schedule = WeeklySchedule.Create(
            new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday }
                .ToDictionary(
                    d => d,
                    _ => (IEnumerable<LocalTimeRange>)
                    [
                        new(new LocalTime(9, 0), new LocalTime(13, 0)),
                        new(new LocalTime(14, 0), new LocalTime(18, 0)),
                    ])).Value;
        var doctor = Doctor.Create(DoctorId.New(), "Тестовый Врач", "Терапевт", schedule).Value;
        doctor.AddService(long30.Id);
        doctor.AddService(short15.Id);

        var patientList = Enumerable.Range(1, patients)
            .Select(i => Patient.Create(PatientId.New(), $"Пациент {i}", PhoneNumber.Create($"+7900{i:D7}").Value, null).Value)
            .ToList();

        await WithDbAsync(async db =>
        {
            db.Services.AddRange(long30, short15);
            db.Doctors.Add(doctor);
            db.Patients.AddRange(patientList);
            return await db.SaveChangesAsync();
        });

        return new TestClinic(doctor.Id, long30.Id, short15.Id, patientList.Select(p => p.Id).ToList());
    }

    /// <summary>Access-токен пациента без входа по паролю: учётная запись создаётся при первом обращении.</summary>
    public Task<string> AccessTokenAsync(PatientId patient) =>
        TokenAsync($"patient:{patient}", now => UserAccount.ForPatient(UserId.New(), TestEmail(), "test-only", patient, now));

    public Task<string> DoctorTokenAsync(DoctorId doctor) =>
        TokenAsync($"doctor:{doctor}", now => UserAccount.ForStaff(UserId.New(), TestEmail(), "test-only", UserRole.Doctor, doctor, now).Value);

    public Task<string> AdminTokenAsync() =>
        TokenAsync("admin", now => UserAccount.ForStaff(UserId.New(), TestEmail(), "test-only", UserRole.Admin, null, now).Value);

    private async Task<string> TokenAsync(string subject, Func<Instant, UserAccount> create)
    {
        var account = await _accounts.GetOrAdd(subject, _ => new Lazy<Task<UserAccount>>(() => WithDbAsync(async db =>
        {
            var created = create(Clock.GetCurrentInstant());
            db.Users.Add(created);
            await db.SaveChangesAsync();
            return created;
        }))).Value;

        // Токен — на текущее время часов теста: тесты сдвигают время.
        return Factory.Services.GetRequiredService<ITokenService>().CreateAccessToken(account, Clock.GetCurrentInstant()).Token;
    }

    private static Email TestEmail() => Email.Create($"{Guid.NewGuid():N}@test.local").Value;

    private static async Task<Result<TResponse>> InvokeAsync<TResponse>(object handler, object request)
    {
        var handle = handler.GetType().GetMethod("Handle")!;
        return await (Task<Result<TResponse>>)handle.Invoke(handler, [request, CancellationToken.None])!;
    }

    private sealed class MedSlotApiFactory(string connectionString, string kafkaBootstrapServers, IClock clock)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Не Development: миграции и топики создаёт фикстура, демо-данные не нужны.
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.UseSetting("Kafka:BootstrapServers", kafkaBootstrapServers);
            builder.UseSetting("Outbox:RelayEnabled", "false");
            builder.UseSetting("Waitlist:ExpiryEnabled", "false");

            // Все запросы тестов идут с одного «адреса»: лимит не должен им мешать.
            // Сам лимит проверяется на производной фабрике с низким порогом.
            builder.UseSetting("RateLimiting:BookingPermitLimit", "100000");
            builder.UseSetting("RateLimiting:AuthPermitLimit", "100000");
            builder.UseSetting("Internal:ServiceKeys:notifications", NotificationsServiceKey);
            builder.ConfigureTestServices(services => services.AddSingleton(clock));
        }
    }
}

public sealed record TestClinic(DoctorId Doctor, ServiceId Long30, ServiceId Short15, IReadOnlyList<PatientId> Patients)
{
    public PatientId Patient => Patients[0];
}
