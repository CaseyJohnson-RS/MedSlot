using System.Security.Claims;
using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using MedSlot.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NodaTime;

namespace MedSlot.Api.Auth;

/// <summary>Политики авторизации по ролям (ADR 0011).</summary>
internal static class Policies
{
    public const string Patient = nameof(UserRole.Patient);
    public const string Doctor = nameof(UserRole.Doctor);
    public const string Admin = nameof(UserRole.Admin);

    /// <summary>Врач или администратор.</summary>
    public const string Staff = "Staff";
}

internal static class AuthSetup
{
    public static IServiceCollection AddMedSlotAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Параметры проверки берутся из DI: тот же ключ, что у выпуска токенов, и те же часы приложения.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<AuthOptions, IClock>((options, auth, clock) =>
            {
                // Claims как в токене (sub, role), без переименования в длинные URI.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = auth.CreateValidationParameters(clock);
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Patient, p => p.RequireRole(Policies.Patient))
            .AddPolicy(Policies.Doctor, p => p.RequireRole(Policies.Doctor))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Policies.Admin))
            .AddPolicy(Policies.Staff, p => p.RequireRole(Policies.Doctor, Policies.Admin));

        return services;
    }
}

/// <summary>Кто выполняет запрос — из claims access-токена.</summary>
internal static class CurrentUser
{
    public static PatientId PatientId(this ClaimsPrincipal user) =>
        new(Guid.Parse(user.FindFirstValue(MedSlotClaims.PatientId)
            ?? throw new InvalidOperationException("В токене нет пациента: эндпоинт должен требовать роль Patient.")));

    /// <summary>Врач из токена или <c>null</c> для администратора.</summary>
    public static DoctorId? DoctorId(this ClaimsPrincipal user) =>
        user.FindFirstValue(MedSlotClaims.DoctorId) is { } id ? new DoctorId(Guid.Parse(id)) : null;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Policies.Admin);
}
