using System.Buffers.Binary;
using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Infrastructure.Idempotency;
using MedSlot.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MedSlot.Infrastructure.Persistence;

internal sealed class UnitOfWork(MedSlotDbContext db) : IUnitOfWork
{
    // Пространства ключей advisory-блокировок (первый аргумент pg_advisory_xact_lock(int, int)).
    private const int DoctorLockSpace = 1;
    private const int PatientLockSpace = 2;

    public async Task<Result> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var locks = BookingLocks();
            if (locks.Count == 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await SaveWithBookingLocksAsync(locks, cancellationToken);
            }

            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApplicationErrors.ConcurrentModification;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ExclusionViolation,
        } postgres)
        {
            return postgres.ConstraintName == AppointmentConstraints.PatientNoOverlap
                ? AppointmentErrors.PatientTimeConflict
                : AppointmentErrors.SlotAlreadyTaken;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdempotencyKeyConfiguration.PrimaryKeyName,
        })
        {
            // Параллельный запрос с тем же ключом идемпотентности успел сохранить его первым.
            return ApplicationErrors.IdempotencyKeyInUse;
        }
    }

    /// <summary>
    /// Новые записи сохраняются под транзакционными advisory-блокировками врача и пациента (ADR 0005).
    /// Гарантию «нет пересечений» по-прежнему даёт exclusion constraint. Блокировки выстраивают
    /// конкурирующие вставки в очередь: без них параллельные вставки пересекающихся строк
    /// ждут друг друга и Postgres прерывает их с 40P01 deadlock detected.
    /// </summary>
    private async Task SaveWithBookingLocksAsync(IReadOnlyList<(int Space, int Key)> locks, CancellationToken cancellationToken)
    {
        // При включённых повторах (EnableRetryOnFailure) явная транзакция должна выполняться
        // целиком внутри стратегии, чтобы повторялась вместе с блокировками.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                foreach (var (space, key) in locks)
                {
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({space}, {key})", ct);
                }

                await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
                await transaction.CommitAsync(ct);
            },
            cancellationToken);

        db.ChangeTracker.AcceptAllChanges();
    }

    /// <summary>
    /// Ключи блокировок для добавляемых записей. Порядок одинаков во всех транзакциях
    /// (сначала врачи, затем пациенты, по возрастанию ключа), поэтому сами блокировки не образуют цикла.
    /// </summary>
    private List<(int Space, int Key)> BookingLocks() =>
        db.ChangeTracker.Entries<Appointment>()
            .Where(e => e.State == EntityState.Added)
            .SelectMany(e => new[]
            {
                (Space: DoctorLockSpace, Key: Fold(e.Entity.DoctorId.Value)),
                (Space: PatientLockSpace, Key: Fold(e.Entity.PatientId.Value)),
            })
            .Distinct()
            .OrderBy(l => l.Space)
            .ThenBy(l => l.Key)
            .ToList();

    /// <summary>
    /// Сворачивает Guid в int для ключа блокировки. Детерминировано во всех процессах.
    /// Коллизия лишь изредка заставит подождать запись к другому врачу — на корректность не влияет.
    /// </summary>
    private static int Fold(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes)
            ^ BinaryPrimitives.ReadInt32LittleEndian(bytes[4..])
            ^ BinaryPrimitives.ReadInt32LittleEndian(bytes[8..])
            ^ BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
    }
}
