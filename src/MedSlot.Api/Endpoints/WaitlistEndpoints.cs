using System.Security.Claims;
using MedSlot.Api.Auth;
using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Api.Idempotency;
using MedSlot.Api.Validation;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Waitlist;
using MedSlot.Domain.Common;
using NodaTime;

namespace MedSlot.Api.Endpoints;

// Пациент берётся из токена; чужие заявки и предложения — «не найдено».

internal static class WaitlistEndpoints
{
    public static IEndpointRouteBuilder MapWaitlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/waitlist").WithTags("Лист ожидания").RequireAuthorization(Policies.Patient);

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
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/offers/{appointmentId:guid}/accept", Accept)
            .WithName("AcceptWaitlistOffer")
            .WithSummary("Принять предложенное время")
            .Idempotent()
            .WithStateChangeResponses();

        group.MapPost("/offers/{appointmentId:guid}/decline", Decline)
            .WithName("DeclineWaitlistOffer")
            .WithSummary("Отказаться от предложенного времени")
            .WithDescription("Заявка остаётся в листе ожидания, но перемещается в конец очереди.")
            .Idempotent()
            .WithStateChangeResponses();

        return app;
    }

    private static async Task<IResult> Join(
        JoinWaitlistRequest request, ClaimsPrincipal user, ICommandHandler<JoinWaitlist, WaitlistEntryId> handler, CancellationToken ct) =>
        (await handler.Handle(
            new JoinWaitlist(
                user.PatientId(),
                new DoctorId(request.DoctorId),
                new ServiceId(request.ServiceId),
                LocalDate.FromDateOnly(request.From),
                LocalDate.FromDateOnly(request.To)),
            ct))
            .ToResponse(id => TypedResults.Created("/api/v1/me/waitlist", new WaitlistEntryCreatedResponse(id.Value)));

    private static async Task<IResult> Cancel(
        Guid entryId, ClaimsPrincipal user, ICommandHandler<CancelWaitlistEntry, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new CancelWaitlistEntry(new WaitlistEntryId(entryId), user.PatientId()), ct));

    private static async Task<IResult> Accept(
        Guid appointmentId, ClaimsPrincipal user, ICommandHandler<AcceptWaitlistOffer, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new AcceptWaitlistOffer(new AppointmentId(appointmentId), user.PatientId()), ct));

    private static async Task<IResult> Decline(
        Guid appointmentId, ClaimsPrincipal user, ICommandHandler<DeclineWaitlistOffer, Unit> handler, CancellationToken ct) =>
        NoContent(await handler.Handle(new DeclineWaitlistOffer(new AppointmentId(appointmentId), user.PatientId()), ct));

    private static IResult NoContent(Result<Unit> result) => result.ToResponse(_ => TypedResults.NoContent());

    private static RouteHandlerBuilder WithStateChangeResponses(this RouteHandlerBuilder builder) =>
        builder
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
