using MedSlot.Api.Auth;
using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Api.Idempotency;
using MedSlot.Api.Validation;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Admin;
using MedSlot.Application.Appointments;
using MedSlot.Application.Identity;
using MedSlot.Domain.Appointments;
using MedSlot.Domain.Common;
using MedSlot.Domain.Identity;
using NodaTime;
using NodaTime.Extensions;

namespace MedSlot.Api.Endpoints;

/// <summary>Операции администратора клиники.</summary>
internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").WithTags("Администрирование").RequireAuthorization(Policies.Admin);

        admin.MapPost("/appointments", Book)
            .WithName("AdminBookAppointment")
            .WithSummary("Записать пациента")
            .WithDescription("Без проверки бана и лимита активных записей; занятость и расписание проверяются.")
            .Validate<AdminBookAppointmentRequest>()
            .Idempotent()
            .Produces<AppointmentCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        admin.MapPost("/staff", CreateStaff)
            .WithName("CreateStaffAccount")
            .WithSummary("Создать учётную запись врача или администратора")
            .Validate<CreateStaffRequest>()
            .Produces<StaffCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        admin.MapPut("/doctors/{doctorId:guid}/schedule", ChangeSchedule)
            .WithName("ChangeWeeklySchedule")
            .WithSummary("Заменить недельное расписание врача")
            .WithDescription(
                "Дни, которых нет в списке, становятся нерабочими. Исключения из расписания сохраняются. Если активные " +
                "записи в горизонте записи окажутся вне нового рабочего времени — 409: сначала отмените их.")
            .Validate<WeeklyScheduleRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPost("/doctors/{doctorId:guid}/schedule-overrides", AddOverride)
            .WithName("AddScheduleOverride")
            .WithSummary("Добавить исключение из расписания")
            .WithDescription(
                "Отпуск, больничный (DayOff) или особые часы (CustomHours). Если на эти даты есть записи вне нового " +
                "рабочего времени — 409: сначала отмените их (отмена клиникой).")
            .Validate<ScheduleOverrideRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapDelete("/doctors/{doctorId:guid}/schedule-overrides", RemoveOverride)
            .WithName("RemoveScheduleOverride")
            .WithSummary("Убрать исключение из расписания")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPost("/patients/{patientId:guid}/lift-ban", LiftBan)
            .WithName("LiftPatientBan")
            .WithSummary("Снять бан пациента")
            .WithDescription("Нарушения до этого момента больше не учитываются.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> Book(
        AdminBookAppointmentRequest request, ICommandHandler<BookAppointment, AppointmentId> handler, CancellationToken ct) =>
        (await handler.Handle(
            new BookAppointment(
                new PatientId(request.PatientId),
                new DoctorId(request.DoctorId),
                new ServiceId(request.ServiceId),
                Instant.FromDateTimeOffset(request.StartsAt),
                BookingSource.Admin),
            ct))
            .ToResponse(AppointmentEndpoints.Created);

    private static async Task<IResult> CreateStaff(
        CreateStaffRequest request, ICommandHandler<CreateStaffAccount, UserId> handler, CancellationToken ct) =>
        (await handler.Handle(
            new CreateStaffAccount(
                request.Email, request.Password, request.Role, request.DoctorId is { } id ? new DoctorId(id) : null),
            ct))
            .ToResponse(id => TypedResults.Created((string?)null, new StaffCreatedResponse(id.Value)));

    private static async Task<IResult> ChangeSchedule(
        Guid doctorId, WeeklyScheduleRequest request, ICommandHandler<ChangeWeeklySchedule, Unit> handler, CancellationToken ct) =>
        (await handler.Handle(
            new ChangeWeeklySchedule(
                new DoctorId(doctorId),
                request.Days.ToDictionary(
                    d => d.Day.ToIsoDayOfWeek(),
                    d => (IReadOnlyList<LocalTimeRange>)d.Hours
                        .Select(h => new LocalTimeRange(LocalTime.FromTimeOnly(h.From), LocalTime.FromTimeOnly(h.To)))
                        .ToList())),
            ct))
            .ToResponse(_ => TypedResults.NoContent());

    private static async Task<IResult> AddOverride(
        Guid doctorId, ScheduleOverrideRequest request, ICommandHandler<AddScheduleOverride, Unit> handler, CancellationToken ct) =>
        (await handler.Handle(
            new AddScheduleOverride(
                new DoctorId(doctorId),
                LocalDate.FromDateOnly(request.From),
                LocalDate.FromDateOnly(request.To),
                request.Kind,
                (request.Hours ?? [])
                    .Select(h => new LocalTimeRange(LocalTime.FromTimeOnly(h.From), LocalTime.FromTimeOnly(h.To)))
                    .ToList(),
                request.Reason),
            ct))
            .ToResponse(_ => TypedResults.NoContent());

    private static async Task<IResult> RemoveOverride(
        Guid doctorId, DateOnly from, DateOnly to, ICommandHandler<RemoveScheduleOverride, Unit> handler, CancellationToken ct) =>
        (await handler.Handle(
            new RemoveScheduleOverride(new DoctorId(doctorId), LocalDate.FromDateOnly(from), LocalDate.FromDateOnly(to)), ct))
            .ToResponse(_ => TypedResults.NoContent());

    private static async Task<IResult> LiftBan(Guid patientId, ICommandHandler<LiftPatientBan, Unit> handler, CancellationToken ct) =>
        (await handler.Handle(new LiftPatientBan(new PatientId(patientId)), ct)).ToResponse(_ => TypedResults.NoContent());
}
