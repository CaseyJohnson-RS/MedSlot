using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Api.Idempotency;
using MedSlot.Api.Validation;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Waitlist;
using MedSlot.Domain.Common;
using NodaTime;

namespace MedSlot.Api.Endpoints;

// Пока нет аутентификации (фаза 7), пациент передаётся явно; чужие заявки и предложения — «не найдено».

internal static class WaitlistEndpoints
{
    public static IEndpointRouteBuilder MapWaitlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/waitlist").WithTags("Лист ожидания");

        group.MapPost("/", Join)
            .WithName("JoinWaitlist")
            .WithSummary("Встать в лист ожидания")
            .WithDescription(
                "Когда у врача освободится подходящее время, пациенту придёт предложение. " +
                "Время удерживается за ним 30 минут; без ответа предложение уходит следующему.")
            .Validate<JoinWaitlistRequest>()
            .Idempotent()
            .Produces<WaitlistEntryCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{entryId:guid}/cancel", Cancel)
            .WithName("CancelWaitlistEntry")
            .WithSummary("Выйти из листа ожидания")
            .WithDescription("Если по заявке ждёт предложение, время отдаётся следующему в очереди.")
            .Validate<WaitlistActionRequest>()
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/offers/{appointmentId:guid}/accept", Accept)
            .WithName("AcceptWaitlistOffer")
            .WithSummary("Принять предложенное время")
            .Validate<WaitlistActionRequest>()
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/offers/{appointmentId:guid}/decline", Decline)
            .WithName("DeclineWaitlistOffer")
            .WithSummary("Отказаться от предложенного времени")
            .WithDescription("Заявка остаётся в листе ожидания, но перемещается в конец очереди.")
            .Validate<WaitlistActionRequest>()
            .Idempotent()
            .WithStateChangeResponses();

        app.MapGroup("/api/v1/patients/{patientId:guid}").WithTags("Пациенты")
            .MapGet("/waitlist", GetPatientWaitlist)
            .WithName("GetPatientWaitlist")
            .WithSummary("Заявки пациента в листе ожидания")
            .WithDescription("Вместе с предложенным временем, если оно ждёт ответа.")
            .Produces<IReadOnlyList<WaitlistEntryResponse>>();

        return app;
    }

    private static async Task<IResult> Join(
        JoinWaitlistRequest request, ICommandHandler<JoinWaitlist, WaitlistEntryId> handler, CancellationToken ct) =>
        (await handler.Handle(
            new JoinWaitlist(
                new PatientId(request.PatientId),
                new DoctorId(request.DoctorId),
                new ServiceId(request.ServiceId),
                LocalDate.FromDateOnly(request.From),
                LocalDate.FromDateOnly(request.To)),
            ct))
            .ToResponse(id => TypedResults.Created(
                $"/api/v1/patients/{request.PatientId}/waitlist", new WaitlistEntryCreatedResponse(id.Value)));

    private static async Task<IResult> Cancel(
        Guid entryId, WaitlistActionRequest request, ICommandHandler<CancelWaitlistEntry, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new CancelWaitlistEntry(new WaitlistEntryId(entryId), new PatientId(request.PatientId)), ct));

    private static async Task<IResult> Accept(
        Guid appointmentId, WaitlistActionRequest request, ICommandHandler<AcceptWaitlistOffer, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new AcceptWaitlistOffer(new AppointmentId(appointmentId), new PatientId(request.PatientId)), ct));

    private static async Task<IResult> Decline(
        Guid appointmentId, WaitlistActionRequest request, ICommandHandler<DeclineWaitlistOffer, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new DeclineWaitlistOffer(new AppointmentId(appointmentId), new PatientId(request.PatientId)), ct));

    private static async Task<IResult> GetPatientWaitlist(
        Guid patientId, IQueryHandler<GetPatientWaitlist, IReadOnlyList<WaitlistEntryDto>> handler, CancellationToken ct) =>
        (await handler.Handle(new GetPatientWaitlist(new PatientId(patientId)), ct))
            .ToResponse(list => TypedResults.Ok(list.Select(WaitlistEntryResponse.Create).ToList()));

    private static IResult NoContent(Result<Unit> result) => result.ToResponse(_ => TypedResults.NoContent());

    private static RouteHandlerBuilder WithStateChangeResponses(this RouteHandlerBuilder builder) =>
        builder
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
