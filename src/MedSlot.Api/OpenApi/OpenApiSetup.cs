using MedSlot.Api.Idempotency;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace MedSlot.Api.OpenApi;

internal static class OpenApiSetup
{
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddMedSlotOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "MedSlot API";
                document.Info.Description =
                    "Онлайн-запись к врачу. Ошибки — в формате ProblemDetails (RFC 9457); " +
                    "машиночитаемый код ошибки — в поле `code`. Моменты времени — в UTC. " +
                    "Вход — `/api/v1/auth/login`; access-токен передаётся в заголовке `Authorization: Bearer …`.";

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access-токен из /api/v1/auth/login или /api/v1/auth/register.",
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;

                // Эндпоинты с авторизацией помечаются схемой Bearer — Scalar покажет поле для токена.
                if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
                    });
                    operation.Responses ??= new OpenApiResponses();
                    operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Нет или недействителен access-токен." });
                    operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Недостаточно прав." });
                }

                if (metadata.OfType<IdempotentEndpointMetadata>().Any())
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = IdempotencyMiddleware.HeaderName,
                        In = ParameterLocation.Header,
                        Required = false,
                        Description =
                            "Ключ идемпотентности (например, UUID). Повтор с тем же ключом и телом вернёт сохранённый ответ " +
                            "с заголовком Idempotent-Replayed; с другим телом — 422; пока первый запрос выполняется — 409. " +
                            "Ключ хранится 24 часа.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 100 },
                    });
                }

                return Task.CompletedTask;
            });
        });
}
