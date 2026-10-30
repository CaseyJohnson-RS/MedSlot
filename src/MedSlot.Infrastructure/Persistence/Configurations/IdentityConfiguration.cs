using MedSlot.Domain.Doctors;
using MedSlot.Domain.Identity;
using MedSlot.Domain.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSlot.Infrastructure.Persistence.Configurations;

internal sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    /// <summary>По имени индекса UnitOfWork узнаёт гонку двух регистраций с одним email.</summary>
    public const string EmailIndex = "ux_users_email";

    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Ignore(u => u.DomainEvents);

        builder.Property(u => u.Email)
            .HasMaxLength(254)
            .HasConversion(email => email.Value, value => Email.Create(value).Value);
        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName(EmailIndex);

        builder.Property(u => u.PasswordHash).HasMaxLength(200);
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);

        // Одна учётная запись на пациента и на врача.
        builder.HasOne<Patient>().WithMany().HasForeignKey(u => u.PatientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(u => u.PatientId).IsUnique();
        builder.HasOne<Doctor>().WithMany().HasForeignKey(u => u.DoctorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(u => u.DoctorId).IsUnique();
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Ignore(t => t.DomainEvents);

        // SHA-256 в hex: сам токен в БД не хранится.
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsFixedLength();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);

        // Два одновременных обновления одним токеном: второе проиграет.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
