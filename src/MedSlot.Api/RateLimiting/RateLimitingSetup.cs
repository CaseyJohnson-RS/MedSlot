using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using MedSlot.Api.Errors;
using MedSlot.Domain.Common;

namespace MedSlot.Api.RateLimiting;

internal static class RateLimitingSetup
{
    public const string BookingPolicy = "booking";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly DomainError Exceeded = new("rate_limit.exceeded", "Слишком много запросов. Повторите позже.");

    /// <summary>
    /// Ограничение частоты записи: фиксированное окно на клиента. Счётчики в памяти процесса —
    /// при нескольких экземплярах API нужен распределённый лимитер (см. ROADMAP, фаза 9).
    /// </summary>
    public static IServiceCollection AddMedSlotRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimiting:BookingPermitLimit", 10);

        return services.AddRateLimiter(options =>
        {
            options.AddPolicy(BookingPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = Window, QueueLimit = 0 }));

            options.OnRejected = async (rejected, _) =>
            {
                rejected.HttpContext.Response.Headers.RetryAfter =
                    ((int)Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await Exceeded.ToProblem().ExecuteAsync(rejected.HttpContext);
            };
        });
    }

    // До аутентификации (фаза 7) клиент определяется по IP, затем — по пользователю.
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}
