using System.Security.Claims;
using MedSlot.Api.Auth;
using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Appointments;
using MedSlot.Application.Patients;
using MedSlot.Application.Waitlist;
using MedSlot.Domain.Common;

namespace MedSlot.Api.Endpoints;

/// <summary>
/// Данные пациента: свои — в <c>/me</c> (пациент из токена), любого пациента — для персонала.
/// Чтение карточки персоналом записывается в журнал доступа (ADR 0012).
/// </summary>
internal static class PatientEndpoints
{
    public static IEndpointRouteBuilder MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Пациенты").RequireAuthorization(Policies.Patient);

        me.MapGet("/card", OwnCard)
            .WithName("GetMyCard")
            .WithSummary("Моя карточка")
            .Produces<PatientCardResponse>();

        me.MapGet("/card-access-log", (ClaimsPrincipal user, IQueryHandler<GetCardAccessLog, IReadOnlyList<CardAccessDto>> h, CancellationToken ct) =>
                AccessLog(user.PatientId(), h, ct))
            .WithName("GetMyCardAccessLog")
            .WithSummary("Кто смотрел мою карточку")
            .WithDescription("Каждое чтение карточки врачом, администратором или внутренним сервисом — с целью обращения.")
            .Produces<IReadOnlyList<CardAccessResponse>>();

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

        var patients = app.MapGroup("/api/v1/patients/{patientId:guid}").WithTags("Пациенты");

        patients.MapGet("/card", StaffCard)
            .WithName("GetPatientCard")
            .WithSummary("Карточка пациента (персонал)")
            .WithDescription(
                "Врач — только своих пациентов (с записью к нему), администратор — любого. " +
                "Параметр purpose обязателен: каждое чтение записывается в журнал доступа.")
            .RequireAuthorization(Policies.Staff)
            .Produces<PatientCardResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        patients.MapGet("/card-access-log", (Guid patientId, IQueryHandler<GetCardAccessLog, IReadOnlyList<CardAccessDto>> h, CancellationToken ct) =>
                AccessLog(new PatientId(patientId), h, ct))
            .WithName("GetPatientCardAccessLog")
            .WithSummary("Журнал доступа к карточке (администратор)")
            .RequireAuthorization(Policies.Admin)
            .Produces<IReadOnlyList<CardAccessResponse>>();

        patients.MapGet("/appointments", (Guid patientId, IQueryHandler<GetPatientAppointments, IReadOnlyList<AppointmentDto>> h, CancellationToken ct) =>
                Appointments(new PatientId(patientId), h, ct))
            .WithName("GetPatientAppointments")
            .WithSummary("Записи пациента (администратор)")
            .RequireAuthorization(Policies.Admin)
            .Produces<IReadOnlyList<AppointmentResponse>>();

        patients.MapGet("/waitlist", (Guid patientId, IQueryHandler<GetPatientWaitlist, IReadOnlyList<WaitlistEntryDto>> h, CancellationToken ct) =>
                Waitlist(new PatientId(patientId), h, ct))
            .WithName("GetPatientWaitlist")
            .WithSummary("Лист ожидания пациента (администратор)")
            .RequireAuthorization(Policies.Admin)
            .Produces<IReadOnlyList<WaitlistEntryResponse>>();

        return app;
    }

    private static async Task<IResult> OwnCard(ClaimsPrincipal user, IQueryHandler<GetOwnPatientCard, PatientCardDto> handler, CancellationToken ct) =>
        (await handler.Handle(new GetOwnPatientCard(user.PatientId()), ct)).ToResponse(card => TypedResults.Ok(PatientCardResponse.Create(card)));

    private static async Task<IResult> StaffCard(
        Guid patientId, string? purpose, ClaimsPrincipal user, ICommandHandler<ViewPatientCard, PatientCardDto> handler, CancellationToken ct)
    {
        CardViewer viewer = user.DoctorId() is { } doctorId
            ? new CardViewer.Doctor(user.UserId(), doctorId)
            : new CardViewer.Admin(user.UserId());

        return (await handler.Handle(new ViewPatientCard(new PatientId(patientId), viewer, purpose ?? string.Empty), ct))
            .ToResponse(card => TypedResults.Ok(PatientCardResponse.Create(card)));
    }

    private static async Task<IResult> AccessLog(
        PatientId patientId, IQueryHandler<GetCardAccessLog, IReadOnlyList<CardAccessDto>> handler, CancellationToken ct) =>
        (await handler.Handle(new GetCardAccessLog(patientId), ct))
            .ToResponse(list => TypedResults.Ok(list.Select(CardAccessResponse.Create).ToList()));

    private static async Task<IResult> Appointments(
        PatientId patientId, IQueryHandler<GetPatientAppointments, IReadOnlyList<AppointmentDto>> handler, CancellationToken ct) =>
        (await handler.Handle(new GetPatientAppointments(patientId), ct))
            .ToResponse(list => TypedResults.Ok(list.Select(AppointmentResponse.Create).ToList()));

    private static async Task<IResult> Waitlist(
        PatientId patientId, IQueryHandler<GetPatientWaitlist, IReadOnlyList<WaitlistEntryDto>> handler, CancellationToken ct) =>
        (await handler.Handle(new GetPatientWaitlist(patientId), ct))
            .ToResponse(list => TypedResults.Ok(list.Select(WaitlistEntryResponse.Create).ToList()));
}
