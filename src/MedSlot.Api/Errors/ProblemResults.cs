using MedSlot.Domain.Common;

namespace MedSlot.Api.Errors;

/// <summary>
/// Доменная ошибка → <c>ProblemDetails</c> (RFC 9457). Стабильный машиночитаемый код ошибки —
/// в расширении <c>code</c>: по нему клиент различает, например, «слот занят» и «устаревшие данные».
/// </summary>
internal static class ProblemResults
{
    public static IResult ToProblem(this DomainError error) =>
        TypedResults.Problem(
            title: error.Message,
            statusCode: StatusCodeFor(error.Code),
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    public static IResult ToResponse<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();

    internal static int StatusCodeFor(string code) => code switch
    {
        _ when code.EndsWith(".not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,

        "patient.booking_banned" => StatusCodes.Status403Forbidden,

        // Конфликт с текущим состоянием: повтор после обновления данных может пройти.
        "appointment.slot_already_taken"
            or "appointment.patient_time_conflict"
            or "appointment.invalid_status"
            or "appointment.offer_expired"
            or "waitlist.invalid_status"
            or "waitlist.duplicate_entry"
            or "waitlist.too_many_entries"
            or "common.concurrent_modification"
            or "idempotency.request_in_progress" => StatusCodes.Status409Conflict,

        "common.invalid_date_range" or "idempotency.invalid_key" => StatusCodes.Status400BadRequest,

        "rate_limit.exceeded" => StatusCodes.Status429TooManyRequests,

        // Запрос корректен по форме, но нарушает правило домена.
        _ => StatusCodes.Status422UnprocessableEntity,
    };
}
