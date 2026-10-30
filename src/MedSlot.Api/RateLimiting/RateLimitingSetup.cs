using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using MedSlot.Api.Errors;
using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;

namespace MedSlot.Api.RateLimiting;

internal static class RateLimitingSetup
{
    public const string BookingPolicy = "booking";

    /// <summary>Вход и регистрация: защита от перебора паролей.</summary>
    public const string AuthPolicy = "auth";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly DomainError Exceeded = new("rate_limit.exceeded", "Слишком много запросов. Повторите позже.");

    /// <summary>
    /// Фиксированное окно на клиента. Счётчики в памяти процесса —
    /// при нескольких экземплярах API нужен распределённый лимитер (см. ROADMAP, фаза 9).
    /// </summary>
    public static IServiceCollection AddMedSlotRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var bookingLimit = configuration.GetValue("RateLimiting:BookingPermitLimit", 10);
        var authLimit = configuration.GetValue("RateLimiting:AuthPermitLimit", 10);

        return services.AddRateLimiter(options =>
        {
            // Запись — на пользователя: он уже вошёл.
            options.AddPolicy(BookingPolicy, context => FixedWindow(UserOrIp(context), bookingLimit));

            // Вход — на IP: пользователя ещё нет, а перебор идёт по разным email.
            options.AddPolicy(AuthPolicy, context => FixedWindow(Ip(context), authLimit));

            options.OnRejected = async (rejected, _) =>
            {
                rejected.HttpContext.Response.Headers.RetryAfter =
                    ((int)Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await Exceeded.ToProblem().ExecuteAsync(rejected.HttpContext);
            };
        });
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = Window, QueueLimit = 0 });

    private static string UserOrIp(HttpContext context) =>
        context.User.FindFirstValue(MedSlotClaims.Subject) is { } user ? $"user:{user}" : Ip(context);

    private static string Ip(HttpContext context) => $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
