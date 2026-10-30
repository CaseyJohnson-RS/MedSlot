using MedSlot.Api.Idempotency;
using Microsoft.OpenApi;

namespace MedSlot.Api.OpenApi;

internal static class OpenApiSetup
{
    public static IServiceCollection AddMedSlotOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "MedSlot API";
                document.Info.Description =
                    "Онлайн-запись к врачу. Ошибки — в формате ProblemDetails (RFC 9457); " +
                    "машиночитаемый код ошибки — в поле `code`. Моменты времени — в UTC.";
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IdempotentEndpointMetadata>().Any())
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
