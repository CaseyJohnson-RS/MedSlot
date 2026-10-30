using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Api.Validation;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Appointments;
using MedSlot.Application.Scheduling;
using MedSlot.Domain.Clinic;
using MedSlot.Domain.Common;
using NodaTime;

namespace MedSlot.Api.Endpoints;

internal static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app)
    {
        // Расписание и свободное время открыты всем: по ним выбирают, куда записаться.
        var doctors = app.MapGroup("/api/v1/doctors/{doctorId:guid}").WithTags("Врачи").AllowAnonymous();

        doctors.MapGet("/schedule", GetSchedule)
            .WithName("GetDoctorSchedule")
            .WithSummary("Расписание врача")
            .WithDescription("Недельное расписание и исключения в локальном времени клиники.")
            .Produces<DoctorScheduleResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        doctors.MapGet("/slots", GetSlots)
            .WithName("GetAvailableSlots")
            .WithSummary("Свободные слоты")
            .WithDescription("Свободные интервалы для услуги на даты from–to (включительно, не больше 31 дня).")
            .Validate<SlotsQuery>()
            .Produces<IReadOnlyList<SlotResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static async Task<IResult> GetSchedule(
        Guid doctorId, IQueryHandler<GetDoctorSchedule, DoctorScheduleDto> handler, ClinicPolicy policy, CancellationToken ct) =>
        (await handler.Handle(new GetDoctorSchedule(new DoctorId(doctorId)), ct))
            .ToResponse(dto => TypedResults.Ok(DoctorScheduleResponse.Create(dto, policy.TimeZone)));

    private static async Task<IResult> GetSlots(
        [AsParameters] SlotsQuery query, IQueryHandler<GetAvailableSlots, IReadOnlyList<SlotDto>> handler, CancellationToken ct) =>
        (await handler.Handle(
            new GetAvailableSlots(
                new DoctorId(query.DoctorId),
                new ServiceId(query.ServiceId),
                LocalDate.FromDateOnly(query.From),
                LocalDate.FromDateOnly(query.To)),
            ct))
            .ToResponse(slots => TypedResults.Ok(slots.Select(SlotResponse.Create).ToList()));
}
