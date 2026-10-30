using MedSlot.Domain.Appointments;
using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using MedSlot.Domain.Waitlist;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSlot.Infrastructure.Persistence.Configurations;

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("appointments", t =>
            t.HasCheckConstraint("ck_appointments_time_range", "ends_at > starts_at"));
        builder.HasKey(a => a.Id);
        builder.Ignore(a => a.DomainEvents);

        builder.ComplexProperty(a => a.Time, time =>
        {
            time.Property(t => t.Start).HasColumnName("starts_at");
            time.Property(t => t.End).HasColumnName("ends_at");
        });

        // Строки, а не числа: значения видны в БД и используются в условии exclusion constraint.
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.BookedBy).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.CancelledBy).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.CancellationReason).HasMaxLength(100);

        // Оптимистичная блокировка по системной колонке xmin: параллельные переходы статуса
        // одной записи не затирают друг друга.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasOne<Doctor>().WithMany().HasForeignKey(a => a.DoctorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Patient>().WithMany().HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Service>().WithMany().HasForeignKey(a => a.ServiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WaitlistEntry>().WithMany().HasForeignKey(a => a.WaitlistEntryId).OnDelete(DeleteBehavior.Restrict);

        // Поиск истёкших удержаний.
        builder.HasIndex(a => a.HoldExpiresAt).HasFilter("status = 'Held'");

        // Подсчёт нарушений пациента (ADR 0004).
        builder.HasIndex(a => new { a.PatientId, a.Status, a.StatusChangedAt });

        // Exclusion constraints на пересечение времени (ADR 0005) создаются в миграции SQL-ом:
        // EF Core не умеет описывать их в модели. Имена — в AppointmentConstraints.
    }
}

internal static class AppointmentConstraints
{
    public const string DoctorNoOverlap = "ex_appointments_doctor_no_overlap";
    public const string PatientNoOverlap = "ex_appointments_patient_no_overlap";
}
