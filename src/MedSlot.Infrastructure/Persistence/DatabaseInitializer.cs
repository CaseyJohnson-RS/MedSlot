using MedSlot.Application.Abstractions;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace MedSlot.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>
    /// Применяет миграции и, если <paramref name="seedDemoData"/>, заполняет пустую БД демо-данными.
    /// Для локальной разработки и тестов; в продакшене миграции применяются отдельным шагом развёртывания.
    /// </summary>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services, bool seedDemoData, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MedSlotDbContext>();

        await db.Database.MigrateAsync(cancellationToken);

        if (!seedDemoData)
        {
            return;
        }

        if (!await db.Doctors.AnyAsync(cancellationToken))
        {
            DemoData.Add(db, scope.ServiceProvider.GetRequiredService<ClinicPolicy>());
            await db.SaveChangesAsync(cancellationToken);
        }

        // Отдельная проверка: БД могла быть заполнена демо-данными до появления учётных записей.
        if (!await db.Users.AnyAsync(cancellationToken) && await db.Doctors.AnyAsync(d => d.Id == DemoData.Therapist, cancellationToken))
        {
            DemoData.AddAccounts(
                db,
                scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
                scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant());
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>Демо-данные с фиксированными id, чтобы их можно было использовать в запросах из Scalar.</summary>
public static class DemoData
{
    public static readonly ServiceId PrimaryVisit = new(Guid.Parse("0199c0de-0000-7000-8000-000000000001"));
    public static readonly ServiceId FollowUpVisit = new(Guid.Parse("0199c0de-0000-7000-8000-000000000002"));

    public static readonly DoctorId Therapist = new(Guid.Parse("0199c0de-0000-7000-8000-000000000101"));
    public static readonly DoctorId Cardiologist = new(Guid.Parse("0199c0de-0000-7000-8000-000000000102"));
    public static readonly DoctorId Neurologist = new(Guid.Parse("0199c0de-0000-7000-8000-000000000103"));

    public static readonly PatientId Alice = new(Guid.Parse("0199c0de-0000-7000-8000-000000000201"));
    public static readonly PatientId Bob = new(Guid.Parse("0199c0de-0000-7000-8000-000000000202"));

    /// <summary>Пароль всех демо-учётных записей. Только для локального стенда.</summary>
    public const string DemoPassword = "Demo-2026!";

    /// <summary>Демо-учётные записи для каждой роли: логины — в README.</summary>
    internal static void AddAccounts(MedSlotDbContext db, IPasswordHasher hasher, Instant now)
    {
        var hash = hasher.Hash(DemoPassword);
        static Email Login(string value) => Email.Create(value).Value;

        db.Users.AddRange(
            UserAccount.ForStaff(UserId.New(), Login("admin@medslot.local"), hash, UserRole.Admin, null, now).Value,
            UserAccount.ForStaff(UserId.New(), Login("ivanova@medslot.local"), hash, UserRole.Doctor, Therapist, now).Value,
            UserAccount.ForStaff(UserId.New(), Login("petrov@medslot.local"), hash, UserRole.Doctor, Cardiologist, now).Value,
            UserAccount.ForStaff(UserId.New(), Login("smirnova@medslot.local"), hash, UserRole.Doctor, Neurologist, now).Value,
            UserAccount.ForPatient(UserId.New(), Login("alice@example.com"), hash, Alice, now),
            UserAccount.ForPatient(UserId.New(), Login("bob@example.com"), hash, Bob, now));
    }

    internal static void Add(MedSlotDbContext db, ClinicPolicy policy)
    {
        var primary = Service.Create(PrimaryVisit, "Первичный приём", Duration.FromMinutes(30), policy).Value;
        var followUp = Service.Create(FollowUpVisit, "Повторный приём", Duration.FromMinutes(15), policy).Value;
        db.Services.AddRange(primary, followUp);

        var weekdays = WeeklySchedule.Create(
            new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday }
                .ToDictionary(
                    day => day,
                    _ => (IEnumerable<LocalTimeRange>)
                    [
                        new(new LocalTime(9, 0), new LocalTime(13, 0)),
                        new(new LocalTime(14, 0), new LocalTime(18, 0)),
                    ])).Value;

        foreach (var (id, name, specialization) in new[]
                 {
                     (Therapist, "Иванова Анна Сергеевна", "Терапевт"),
                     (Cardiologist, "Петров Олег Игоревич", "Кардиолог"),
                     (Neurologist, "Смирнова Мария Павловна", "Невролог"),
                 })
        {
            var doctor = Doctor.Create(id, name, specialization, weekdays).Value;
            doctor.AddService(primary.Id);
            doctor.AddService(followUp.Id);
            db.Doctors.Add(doctor);
        }

        // У Алисы есть email — уведомления придут письмом (Mailpit); у Бориса нет — сработает заглушка SMS.
        db.Patients.AddRange(
            Patient.Create(Alice, "Алиса Тестова", PhoneNumber.Create("+7 900 000-00-01").Value, Email.Create("alice@example.com").Value).Value,
            Patient.Create(Bob, "Борис Тестов", PhoneNumber.Create("+7 900 000-00-02").Value, null).Value);
    }
}
