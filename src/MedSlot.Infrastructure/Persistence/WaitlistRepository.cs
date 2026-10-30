using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;
using MedSlot.Domain.Waitlist;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace MedSlot.Infrastructure.Persistence;

internal sealed class WaitlistRepository(MedSlotDbContext db) : IWaitlistRepository
{
    public Task<WaitlistEntry?> GetByIdAsync(WaitlistEntryId id, CancellationToken cancellationToken) =>
        db.WaitlistEntries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Add(WaitlistEntry entry) => db.WaitlistEntries.Add(entry);

    public async Task<IReadOnlyList<WaitlistEntry>> GetQueueAsync(DoctorId doctorId, LocalDate day, CancellationToken cancellationToken) =>
        await db.WaitlistEntries
            .Where(e => e.DoctorId == doctorId
                && e.Status == WaitlistEntryStatus.Active
                && e.From <= day
                && e.To >= day)
            .OrderBy(e => e.QueuedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

    public Task<int> CountOpenAsync(PatientId patientId, CancellationToken cancellationToken) =>
        db.WaitlistEntries.CountAsync(
            e => e.PatientId == patientId && (e.Status == WaitlistEntryStatus.Active || e.Status == WaitlistEntryStatus.Offered),
            cancellationToken);

    public Task<bool> HasOpenAsync(PatientId patientId, DoctorId doctorId, ServiceId serviceId, CancellationToken cancellationToken) =>
        db.WaitlistEntries.AnyAsync(
            e => e.PatientId == patientId
                && e.DoctorId == doctorId
                && e.ServiceId == serviceId
                && (e.Status == WaitlistEntryStatus.Active || e.Status == WaitlistEntryStatus.Offered),
            cancellationToken);

    public async Task<IReadOnlyList<WaitlistEntry>> GetByPatientAsync(PatientId patientId, CancellationToken cancellationToken) =>
        await db.WaitlistEntries.AsNoTracking().Where(e => e.PatientId == patientId).ToListAsync(cancellationToken);

    public Task<int> ExpireStaleAsync(LocalDate today, CancellationToken cancellationToken) =>
        db.WaitlistEntries
            .Where(e => e.Status == WaitlistEntryStatus.Active && e.To < today)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, WaitlistEntryStatus.Expired), cancellationToken);
}
