using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using MedSlot.Domain.Waitlist;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSlot.Infrastructure.Persistence.Configurations;

internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries");
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.DomainEvents);
        builder.Ignore(e => e.Dates);
        builder.Ignore(e => e.IsOpen);

        // from/to — ключевые слова SQL.
        builder.Property(e => e.From).HasColumnName("date_from");
        builder.Property(e => e.To).HasColumnName("date_to");
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

        // Свойство только с геттером EF по соглашению не отображает — указываем явно.
        builder.Property(e => e.CreatedAt);

        // Отказ, истечение и принятие предложения могут прийти одновременно.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasOne<Patient>().WithMany().HasForeignKey(e => e.PatientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Doctor>().WithMany().HasForeignKey(e => e.DoctorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Service>().WithMany().HasForeignKey(e => e.ServiceId).OnDelete(DeleteBehavior.Restrict);

        // Очередь к врачу.
        builder.HasIndex(e => new { e.DoctorId, e.Status, e.QueuedAt });

        // Одна открытая заявка пациента к врачу на услугу — вторая линия защиты после проверки в обработчике.
        builder.HasIndex(e => new { e.PatientId, e.DoctorId, e.ServiceId })
            .IsUnique()
            .HasDatabaseName("ux_waitlist_entries_open")
            .HasFilter("status IN ('Active', 'Offered')");
    }
}
