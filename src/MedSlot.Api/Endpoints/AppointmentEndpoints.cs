using System.Security.Claims;
using MedSlot.Api.Auth;
using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Api.Idempotency;
using MedSlot.Api.RateLimiting;
using MedSlot.Api.Validation;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Appointments;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using NodaTime;

namespace MedSlot.Api.Endpoints;

// Пациент берётся из токена. Врач действует только со своими приёмами, администратор — с любыми.
// Чужая запись неотличима от несуществующей (404): не раскрываем, что такой id есть.

internal static class AppointmentEndpoints
{
    public static IEndpointRouteBuilder MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/appointments").WithTags("Записи");

        group.MapPost("/", Book)
            .WithName("BookAppointment")
            .WithSummary("Записаться к врачу")
            .WithDescription("Время начала должно совпадать с одним из свободных слотов врача для услуги.")
            .RequireAuthorization(Policies.Patient)
            .Validate<BookAppointmentRequest>()
            .Idempotent()
            .RequireRateLimiting(RateLimitingSetup.BookingPolicy)
            .Produces<AppointmentCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/{appointmentId:guid}", Get)
            .WithName("GetAppointment")
            .WithSummary("Запись по id")
            .WithDescription("Доступна её пациенту, её врачу и администратору.")
            .RequireAuthorization()
            .Produces<AppointmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{appointmentId:guid}/cancel", CancelByPatient)
            .WithName("CancelAppointmentByPatient")
            .WithSummary("Отменить свою запись")
            .WithDescription("Отмена позже чем за 24 часа до приёма считается нарушением.")
            .RequireAuthorization(Policies.Patient)
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/{appointmentId:guid}/clinic-cancel", CancelByClinic)
            .WithName("CancelAppointmentByClinic")
            .WithSummary("Отменить запись (клиника)")
            .WithDescription("Врач — только свои приёмы, администратор — любые. Не считается нарушением пациента.")
            .RequireAuthorization(Policies.Staff)
            .Validate<CancelByClinicRequest>()
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/{appointmentId:guid}/complete", Complete)
            .WithName("CompleteAppointment")
            .WithSummary("Отметить приём состоявшимся")
            .RequireAuthorization(Policies.Staff)
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/{appointmentId:guid}/no-show", MarkNoShow)
            .WithName("MarkAppointmentNoShow")
            .WithSummary("Отметить неявку")
            .RequireAuthorization(Policies.Staff)
            .Idempotent()
            .WithStateChangeResponses();

        return app;
    }

    private static async Task<IResult> Book(
        BookAppointmentRequest request, ClaimsPrincipal user, ICommandHandler<BookAppointment, AppointmentId> handler, CancellationToken ct)
    {
        var command = new BookAppointment(
            user.PatientId(),
            new DoctorId(request.DoctorId),
            new ServiceId(request.ServiceId),
            Instant.FromDateTimeOffset(request.StartsAt),
            BookingSource.Patient);

        return (await handler.Handle(command, ct)).ToResponse(Created);
    }

    private static async Task<IResult> Get(
        Guid appointmentId, ClaimsPrincipal user, IQueryHandler<GetAppointment, AppointmentDto> handler, CancellationToken ct)
    {
        var result = await handler.Handle(new GetAppointment(new AppointmentId(appointmentId)), ct);
        if (result.IsSuccess && !CanView(user, result.Value))
        {
            return ApplicationErrors.NotFound("appointment", appointmentId).ToProblem();
        }

        return result.ToResponse(dto => TypedResults.Ok(AppointmentResponse.Create(dto)));
    }

    private static async Task<IResult> CancelByPatient(
        Guid appointmentId, ClaimsPrincipal user, ICommandHandler<CancelAppointmentByPatient, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new CancelAppointmentByPatient(new AppointmentId(appointmentId), user.PatientId()), ct));

    private static async Task<IResult> CancelByClinic(
        Guid appointmentId,
        CancelByClinicRequest request,
        ClaimsPrincipal user,
        ICommandHandler<CancelAppointmentByClinic, Unit> handler,
        CancellationToken ct) =>
        NoContent(await handler.Handle(
            new CancelAppointmentByClinic(new AppointmentId(appointmentId), request.ReasonCode, user.DoctorId()), ct));

    private static async Task<IResult> Complete(
        Guid appointmentId, ClaimsPrincipal user, ICommandHandler<CompleteAppointment, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new CompleteAppointment(new AppointmentId(appointmentId), user.DoctorId()), ct));

    private static async Task<IResult> MarkNoShow(
        Guid appointmentId, ClaimsPrincipal user, ICommandHandler<MarkAppointmentNoShow, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new MarkAppointmentNoShow(new AppointmentId(appointmentId), user.DoctorId()), ct));

    internal static IResult Created(AppointmentId id) =>
        TypedResults.CreatedAtRoute(new AppointmentCreatedResponse(id.Value), "GetAppointment", new { appointmentId = id.Value });

    private static bool CanView(ClaimsPrincipal user, AppointmentDto appointment) =>
        user.IsAdmin()
        || (user.IsInRole(Policies.Patient) && user.PatientId() == appointment.PatientId)
        || user.DoctorId() == appointment.DoctorId;

    private static IResult NoContent(Result<Unit> result) => result.ToResponse(_ => TypedResults.NoContent());

    private static RouteHandlerBuilder WithStateChangeResponses(this RouteHandlerBuilder builder) =>
        builder
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
