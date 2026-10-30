using System.Security.Claims;
using MedSlot.Api.Auth;
using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Patients;
using MedSlot.Domain.Common;

namespace MedSlot.Api.Endpoints;

/// <summary>
/// Внутренний API для сервисов системы (ADR 0012). Доступ — по ключу сервиса, не по токену пользователя;
/// в публичную документацию не входит.
/// </summary>
internal static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/v1").RequireAuthorization(Policies.Service).ExcludeFromDescription();

        // Контакты для уведомлений: обращение записывается в журнал доступа от имени сервиса.
        group.MapGet("/patients/{patientId:guid}/contacts", Contacts);

        return app;
    }

    private static async Task<IResult> Contacts(
        Guid patientId, string? purpose, ClaimsPrincipal service, ICommandHandler<ViewPatientCard, PatientCardDto> handler, CancellationToken ct)
    {
        var name = service.FindFirstValue(ServiceKeyAuthenticationHandler.ServiceClaim)
            ?? throw new InvalidOperationException("Нет имени сервиса: эндпоинт должен требовать политику Service.");

        return (await handler.Handle(new ViewPatientCard(new PatientId(patientId), new CardViewer.Service(name), purpose ?? string.Empty), ct))
            .ToResponse(card => TypedResults.Ok(PatientCardResponse.Create(card)));
    }
}
