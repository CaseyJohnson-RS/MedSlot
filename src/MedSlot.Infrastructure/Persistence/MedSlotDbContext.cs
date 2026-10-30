using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using MedSlot.Domain.Waitlist;
using MedSlot.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;

namespace MedSlot.Infrastructure.Persistence;

public sealed class MedSlotDbContext(DbContextOptions<MedSlotDbContext> options) : DbContext(options)
{
    public DbSet<Doctor> Doctors => Set<Doctor>();

    public DbSet<Service> Services => Set<Service>();

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MedSlotDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DoctorId>().HaveConversion<DoctorIdConverter>();
        configurationBuilder.Properties<PatientId>().HaveConversion<PatientIdConverter>();
        configurationBuilder.Properties<ServiceId>().HaveConversion<ServiceIdConverter>();
        configurationBuilder.Properties<AppointmentId>().HaveConversion<AppointmentIdConverter>();
        configurationBuilder.Properties<WaitlistEntryId>().HaveConversion<WaitlistEntryIdConverter>();
        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();
        configurationBuilder.Properties<RefreshTokenId>().HaveConversion<RefreshTokenIdConverter>();
    }
}
