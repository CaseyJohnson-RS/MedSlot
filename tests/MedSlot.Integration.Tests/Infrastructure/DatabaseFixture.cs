using MedSlot.Application.Abstractions;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using MedSlot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodaTime.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(MedSlot.Integration.Tests.Infrastructure.DatabaseFixture))]


namespace MedSlot.Integration.Tests.Infrastructure;

/// <summary>Один Postgres в контейнере и одно приложение на все интеграционные тесты.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public static readonly DateTimeZone Moscow = DateTimeZoneProviders.Tzdb["Europe/Moscow"];

    /// <summary>Понедельник, 09:00 по Москве.</summary>
    public static readonly Instant BaseNow = Msk(new LocalDate(2026, 11, 2), 9);

    /// <summary>Понедельник через неделю после <see cref="BaseNow"/>: в горизонте записи.</summary>
    public static readonly LocalDate NextMonday = new(2026, 11, 9);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine").Build();

    private MedSlotApiFactory? _factory;

    public FakeClock Clock { get; } = new(BaseNow);

    public ClinicPolicy Policy { get; } = ClinicPolicy.Default;

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Фикстура не инициализирована.");

    public static Instant Msk(LocalDate date, int hour, int minute = 0) =>
        date.At(new LocalTime(hour, minute)).InZoneStrictly(Moscow).ToInstant();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new MedSlotApiFactory(_postgres.GetConnectionString(), Clock);
        await _factory.Services.InitializeDatabaseAsync(seedDemoData: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
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

    private static async Task<Result<TResponse>> InvokeAsync<TResponse>(object handler, object request)
    {
        var handle = handler.GetType().GetMethod("Handle")!;
        return await (Task<Result<TResponse>>)handle.Invoke(handler, [request, CancellationToken.None])!;
    }

    private sealed class MedSlotApiFactory(string connectionString, IClock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Не Development: миграции применяет фикстура, демо-данные не нужны.
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);

            // Все запросы тестов идут с одного «адреса»: лимит не должен им мешать.
            // Сам лимит проверяется на производной фабрике с низким порогом.
            builder.UseSetting("RateLimiting:BookingPermitLimit", "100000");
            builder.ConfigureTestServices(services => services.AddSingleton(clock));
        }
    }
}

public sealed record TestClinic(DoctorId Doctor, ServiceId Long30, ServiceId Short15, IReadOnlyList<PatientId> Patients)
{
    public PatientId Patient => Patients[0];
}
