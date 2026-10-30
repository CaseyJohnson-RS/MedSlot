using System.Security.Claims;
using MedSlot.Api.Auth;
using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Appointments;
using MedSlot.Application.Waitlist;
using MedSlot.Domain.Common;

namespace MedSlot.Api.Endpoints;

/// <summary>
/// Данные пациента: свои — в <c>/me</c> (пациент из токена), любого пациента — для администратора.
/// </summary>
internal static class PatientEndpoints
{
    public static IEndpointRouteBuilder MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Пациенты").RequireAuthorization(Policies.Patient);

        me.MapGet("/appointments", (ClaimsPrincipal user, IQueryHandler<GetPatientAppointments, IReadOnlyList<AppointmentDto>> h, CancellationToken ct) =>
                Appointments(user.PatientId(), h, ct))
            .WithName("GetMyAppointments")
            .WithSummary("Мои записи")
            .Produces<IReadOnlyList<AppointmentResponse>>();

        me.MapGet("/waitlist", (ClaimsPrincipal user, IQueryHandler<GetPatientWaitlist, IReadOnlyList<WaitlistEntryDto>> h, CancellationToken ct) =>
                Waitlist(user.PatientId(), h, ct))
            .WithName("GetMyWaitlist")
            .WithSummary("Мои заявки в листе ожидания")
            .WithDescription("Вместе с предложенным временем, если оно ждёт ответа.")
            .Produces<IReadOnlyList<WaitlistEntryResponse>>();

        var patients = app.MapGroup("/api/v1/patients/{patientId:guid}").WithTags("Пациенты").RequireAuthorization(Policies.Admin);

        patients.MapGet("/appointments", (Guid patientId, IQueryHandler<GetPatientAppointments, IReadOnlyList<AppointmentDto>> h, CancellationToken ct) =>
                Appointments(new PatientId(patientId), h, ct))
            .WithName("GetPatientAppointments")
            .WithSummary("Записи пациента (администратор)")
            .Produces<IReadOnlyList<AppointmentResponse>>();

        patients.MapGet("/waitlist", (Guid patientId, IQueryHandler<GetPatientWaitlist, IReadOnlyList<WaitlistEntryDto>> h, CancellationToken ct) =>
                Waitlist(new PatientId(patientId), h, ct))
            .WithName("GetPatientWaitlist")
            .WithSummary("Лист ожидания пациента (администратор)")
            .Produces<IReadOnlyList<WaitlistEntryResponse>>();

        return app;
    }

    private static async Task<IResult> Appointments(
        PatientId patientId, IQueryHandler<GetPatientAppointments, IReadOnlyList<AppointmentDto>> handler, CancellationToken ct) =>
        (await handler.Handle(new GetPatientAppointments(patientId), ct))
            .ToResponse(list => TypedResults.Ok(list.Select(AppointmentResponse.Create).ToList()));

    private static async Task<IResult> Waitlist(
        PatientId patientId, IQueryHandler<GetPatientWaitlist, IReadOnlyList<WaitlistEntryDto>> handler, CancellationToken ct) =>
        (await handler.Handle(new GetPatientWaitlist(patientId), ct))
            .ToResponse(list => TypedResults.Ok(list.Select(WaitlistEntryResponse.Create).ToList()));
}
