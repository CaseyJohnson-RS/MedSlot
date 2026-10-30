using MedSlot.Domain.Patients;
using MedSlot.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSlot.Infrastructure.Persistence.Configurations;

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("services");
        builder.HasKey(s => s.Id);
        builder.Ignore(s => s.DomainEvents);

        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.Duration).HasColumnType("interval");
    }
}

internal sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> builder)
    {
        builder.ToTable("patients");
        builder.HasKey(p => p.Id);
        builder.Ignore(p => p.DomainEvents);

        builder.Property(p => p.FullName).HasMaxLength(200);

        // Значения уже нормализованы при создании, поэтому чтение через фабрику всегда успешно.
        builder.Property(p => p.Phone)
            .HasMaxLength(16)
            .HasConversion(phone => phone.Value, value => PhoneNumber.Create(value).Value);
        builder.Property(p => p.Email)
            .HasMaxLength(254)
            .HasConversion(email => email!.Value, value => Email.Create(value).Value);
    }
}
