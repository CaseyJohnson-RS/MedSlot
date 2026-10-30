using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MedSlot.Api.Errors;
using MedSlot.Application.Abstractions;
using MedSlot.Domain.Common;
using MedSlot.Infrastructure.Idempotency;

namespace MedSlot.Api.Idempotency;

/// <summary>Метка эндпоинта, который поддерживает заголовок <c>Idempotency-Key</c>.</summary>
internal sealed class IdempotentEndpointMetadata;

internal static class IdempotencyExtensions
{
    public static RouteHandlerBuilder Idempotent(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new IdempotentEndpointMetadata());
}

/// <summary>
/// Обработка <c>Idempotency-Key</c> (ADR 0007). Заголовок необязателен: без него запрос выполняется как обычно.
/// <list type="bullet">
/// <item>Ключ занимается в той же транзакции, что и операция: <see cref="IIdempotencyStore.Begin"/> лишь добавляет
/// строку в контекст, её сохраняет SaveChanges обработчика.</item>
/// <item>Повтор с тем же ключом и тем же запросом — сохранённый ответ и заголовок <c>Idempotent-Replayed: true</c>.</item>
/// <item>Тот же ключ с другим запросом — 422; ключ, запрос по которому ещё выполняется, — 409.</item>
/// <item>Сохраняются только успешные (2xx) ответы. Отказ не сохраняется: повтор выполнит запрос заново.</item>
/// </list>
/// </summary>
internal sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    // До аутентификации (фаза 7) все клиенты в одной области ключей.
    private const string AnonymousScope = "anonymous";

    private static readonly DomainError InvalidKey = new(
        "idempotency.invalid_key", $"Заголовок {HeaderName} должен быть непустой строкой не длиннее {IdempotencyLimits.MaxKeyLength} символов.");

    private static readonly DomainError KeyReused = new(
        "idempotency.key_reused", "Этот ключ идемпотентности уже использован с другим запросом.");

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentEndpointMetadata>() is null
            || !context.Request.Headers.TryGetValue(HeaderName, out var header))
        {
            await next(context);
            return;
        }

        var key = header.ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > IdempotencyLimits.MaxKeyLength)
        {
            await InvalidKey.ToProblem().ExecuteAsync(context);
            return;
        }

        var ct = context.RequestAborted;
        var scope = context.User.FindFirstValue(MedSlotClaims.Subject) ?? AnonymousScope;
        var hash = await HashRequestAsync(context.Request, ct);

        if (await store.FindAsync(scope, key, ct) is { } existing)
        {
            await RespondToRepeatAsync(context, existing, hash);
            return;
        }

        store.Begin(scope, key, hash);

        // Ответ буферизуется, чтобы сохранить его и при необходимости заменить.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);

            if (context.Response.StatusCode is >= 200 and < 300)
            {
                await store.CompleteAsync(
                    scope,
                    key,
                    new IdempotentResponse(
                        context.Response.StatusCode,
                        context.Response.ContentType,
                        context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
                        Encoding.UTF8.GetString(buffer.ToArray())),
                    ct);
            }
            else if (await store.FindAsync(scope, key, ct) is { } concurrent)
            {
                // Операция не сохранилась, а ключ в БД есть: его занял параллельный запрос с тем же ключом.
                // Отвечаем так, как ответили бы на повтор после него.
                context.Response.Clear();
                await RespondToRepeatAsync(context, concurrent, hash);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, ct);
    }

    private static async Task RespondToRepeatAsync(HttpContext context, StoredIdempotencyKey existing, string hash)
    {
        if (existing.RequestHash != hash)
        {
            await KeyReused.ToProblem().ExecuteAsync(context);
            return;
        }

        if (existing.Response is not { } response)
        {
            await ApplicationErrors.IdempotencyKeyInUse.ToProblem().ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        if (response.Location is not null)
        {
            context.Response.Headers.Location = response.Location;
        }

        context.Response.Headers[ReplayedHeaderName] = "true";
        await context.Response.WriteAsync(response.Body, context.RequestAborted);
    }

    /// <summary>SHA-256 от метода, пути с query и тела запроса.</summary>
    private static async Task<string> HashRequestAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));

        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            hash.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
