using MedSlot.Application.Abstractions;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace MedSlot.Infrastructure.Persistence;

internal sealed class DoctorRepository(MedSlotDbContext db) : IDoctorRepository
{
    public Task<Doctor?> GetByIdAsync(DoctorId id, CancellationToken cancellationToken) =>
        db.Doctors.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
}

internal sealed class ServiceRepository(MedSlotDbContext db) : IServiceRepository
{
    public Task<Service?> GetByIdAsync(ServiceId id, CancellationToken cancellationToken) =>
        db.Services.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
}

internal sealed class PatientRepository(MedSlotDbContext db) : IPatientRepository
{
    public Task<Patient?> GetByIdAsync(PatientId id, CancellationToken cancellationToken) =>
        db.Patients.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
}

internal sealed class AppointmentRepository(MedSlotDbContext db) : IAppointmentRepository
{
    public Task<Appointment?> GetByIdAsync(AppointmentId id, CancellationToken cancellationToken) =>
        db.Appointments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Add(Appointment appointment) => db.Appointments.Add(appointment);

    public async Task<IReadOnlyList<TimeRange>> GetBusyRangesAsync(
        DoctorId doctorId, TimeRange window, CancellationToken cancellationToken)
    {
        var rows = await db.Appointments
            .AsNoTracking()
            .Where(a => a.DoctorId == doctorId
                && (a.Status == AppointmentStatus.Held || a.Status == AppointmentStatus.Booked)
                && a.Time.Start < window.End
                && a.Time.End > window.Start)
            .Select(a => new { a.Time.Start, a.Time.End })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new TimeRange(r.Start, r.End)).ToList();
    }

    public async Task<IReadOnlyList<Instant>> GetViolationTimesAsync(
        PatientId patientId, Instant since, CancellationToken cancellationToken) =>
        await db.Appointments
            .AsNoTracking()
            .Where(a => a.PatientId == patientId
                && (a.Status == AppointmentStatus.LateCancelled || a.Status == AppointmentStatus.NoShow)
                && a.StatusChangedAt > since)
            .Select(a => a.StatusChangedAt)
            .ToListAsync(cancellationToken);

    public Task<int> CountUpcomingActiveAsync(PatientId patientId, Instant now, CancellationToken cancellationToken) =>
        db.Appointments.CountAsync(
            a => a.PatientId == patientId
                && (a.Status == AppointmentStatus.Held || a.Status == AppointmentStatus.Booked)
                && a.Time.Start > now,
            cancellationToken);

    public async Task<IReadOnlyList<Appointment>> GetByPatientAsync(PatientId patientId, CancellationToken cancellationToken) =>
        await db.Appointments
            .AsNoTracking()
            .Where(a => a.PatientId == patientId)
            .ToListAsync(cancellationToken);
}
