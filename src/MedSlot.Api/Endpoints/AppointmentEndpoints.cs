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

// Пока нет аутентификации (фаза 7), пациент передаётся явно. Затем он будет браться из токена,
// а права врача и администратора — из ролей.

internal static class AppointmentEndpoints
{
    public static IEndpointRouteBuilder MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/appointments").WithTags("Записи");

        group.MapPost("/", Book)
            .WithName("BookAppointment")
            .WithSummary("Записаться к врачу")
            .WithDescription("Время начала должно совпадать с одним из свободных слотов врача для услуги.")
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
            .Produces<AppointmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{appointmentId:guid}/cancel", CancelByPatient)
            .WithName("CancelAppointmentByPatient")
            .WithSummary("Отменить запись (пациент)")
            .WithDescription("Отмена позже чем за 24 часа до приёма считается нарушением.")
            .Validate<CancelByPatientRequest>()
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/{appointmentId:guid}/clinic-cancel", CancelByClinic)
            .WithName("CancelAppointmentByClinic")
            .WithSummary("Отменить запись (клиника)")
            .WithDescription("Не считается нарушением пациента.")
            .Validate<CancelByClinicRequest>()
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/{appointmentId:guid}/complete", Complete)
            .WithName("CompleteAppointment")
            .WithSummary("Отметить приём состоявшимся")
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/{appointmentId:guid}/no-show", MarkNoShow)
            .WithName("MarkAppointmentNoShow")
            .WithSummary("Отметить неявку")
            .Idempotent()
            .WithStateChangeResponses();

        return app;
    }

    private static async Task<IResult> Book(
        BookAppointmentRequest request, ICommandHandler<BookAppointment, AppointmentId> handler, CancellationToken ct)
    {
        var command = new BookAppointment(
            new PatientId(request.PatientId),
            new DoctorId(request.DoctorId),
            new ServiceId(request.ServiceId),
            Instant.FromDateTimeOffset(request.StartsAt),
            BookingSource.Patient);

        return (await handler.Handle(command, ct)).ToResponse(id => TypedResults.CreatedAtRoute(
            new AppointmentCreatedResponse(id.Value), "GetAppointment", new { appointmentId = id.Value }));
    }

    private static async Task<IResult> Get(Guid appointmentId, IQueryHandler<GetAppointment, AppointmentDto> handler, CancellationToken ct) =>
        (await handler.Handle(new GetAppointment(new AppointmentId(appointmentId)), ct))
            .ToResponse(dto => TypedResults.Ok(AppointmentResponse.Create(dto)));

    private static async Task<IResult> CancelByPatient(
        Guid appointmentId, CancelByPatientRequest request, ICommandHandler<CancelAppointmentByPatient, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new CancelAppointmentByPatient(new AppointmentId(appointmentId), new PatientId(request.PatientId)), ct));

    private static async Task<IResult> CancelByClinic(
        Guid appointmentId, CancelByClinicRequest request, ICommandHandler<CancelAppointmentByClinic, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new CancelAppointmentByClinic(new AppointmentId(appointmentId), request.ReasonCode), ct));

    private static async Task<IResult> Complete(Guid appointmentId, ICommandHandler<CompleteAppointment, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new CompleteAppointment(new AppointmentId(appointmentId)), ct));

    private static async Task<IResult> MarkNoShow(Guid appointmentId, ICommandHandler<MarkAppointmentNoShow, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new MarkAppointmentNoShow(new AppointmentId(appointmentId)), ct));

    private static IResult NoContent(Result<Unit> result) => result.ToResponse(_ => TypedResults.NoContent());

    private static RouteHandlerBuilder WithStateChangeResponses(this RouteHandlerBuilder builder) =>
        builder
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
