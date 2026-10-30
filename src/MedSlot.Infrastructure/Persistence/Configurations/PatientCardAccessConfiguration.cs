using MedSlot.Domain.Doctors;
using MedSlot.Domain.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSlot.Infrastructure.Persistence.Configurations;

internal sealed class PatientCardAccessConfiguration : IEntityTypeConfiguration<PatientCardAccess>
{
    public void Configure(EntityTypeBuilder<PatientCardAccess> builder)
    {
        // Только пополняется: UPDATE, DELETE и TRUNCATE запрещены триггером в миграции.
        builder.ToTable("patient_card_access");
        builder.HasKey(a => a.Id);
        builder.Ignore(a => a.DomainEvents);

        builder.Property(a => a.AccessorKind).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.ServiceName).HasMaxLength(100);
        builder.Property(a => a.Purpose).HasMaxLength(PatientCardAccess.MaxPurposeLength);

        builder.HasOne<Patient>().WithMany().HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Doctor>().WithMany().HasForeignKey(a => a.DoctorId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.PatientId, a.AccessedAt });
    }
}
