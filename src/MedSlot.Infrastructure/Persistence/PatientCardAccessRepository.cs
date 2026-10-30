using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;
using MedSlot.Domain.Patients;
using Microsoft.EntityFrameworkCore;

namespace MedSlot.Infrastructure.Persistence;

internal sealed class PatientCardAccessRepository(MedSlotDbContext db) : IPatientCardAccessRepository
{
    public void Add(PatientCardAccess access) => db.PatientCardAccess.Add(access);

    public async Task<IReadOnlyList<PatientCardAccess>> GetForPatientAsync(PatientId patientId, CancellationToken cancellationToken) =>
        await db.PatientCardAccess.AsNoTracking().Where(a => a.PatientId == patientId).ToListAsync(cancellationToken);
}
