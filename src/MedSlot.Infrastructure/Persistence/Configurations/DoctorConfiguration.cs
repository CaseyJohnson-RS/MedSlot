using MedSlot.Domain.Common;
using MedSlot.Domain.Doctors;
using MedSlot.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSlot.Infrastructure.Persistence.Configurations;

internal sealed class DoctorConfiguration : IEntityTypeConfiguration<Doctor>
{
    public void Configure(EntityTypeBuilder<Doctor> builder)
    {
        builder.ToTable("doctors");
        builder.HasKey(d => d.Id);
        builder.Ignore(d => d.DomainEvents);

        builder.Property(d => d.FullName).HasMaxLength(200);
        builder.Property(d => d.Specialization).HasMaxLength(100);

        // Расписание неизменяемо и заменяется целиком: достаточно сравнения по ссылке.
        builder.Property(d => d.Schedule)
            .HasColumnType("jsonb")
            .HasConversion(s => ScheduleJson.SerializeWeekly(s), json => ScheduleJson.DeserializeWeekly(json));

        // Коллекции живут в приватных полях агрегата; EF пишет в них напрямую.
        builder.Ignore(d => d.Overrides);
        builder.Property<List<ScheduleOverride>>("_overrides")
            .HasColumnName("schedule_overrides")
            .HasColumnType("jsonb")
            .HasConversion(
                o => ScheduleJson.SerializeOverrides(o),
                json => ScheduleJson.DeserializeOverrides(json),
                new ValueComparer<List<ScheduleOverride>>(
                    (a, b) => a!.SequenceEqual(b!),
                    o => o.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    o => o.ToList()));

        builder.Ignore(d => d.Services);
        builder.Property<HashSet<ServiceId>>("_services")
            .HasColumnName("service_ids")
            .HasConversion(
                s => s.Select(id => id.Value).ToArray(),
                ids => ids.Select(id => new ServiceId(id)).ToHashSet(),
                new ValueComparer<HashSet<ServiceId>>(
                    (a, b) => a!.SetEquals(b!),
                    s => s.Aggregate(0, (hash, id) => hash ^ id.GetHashCode()),
                    s => s.ToHashSet()));
    }
}
