using MedSlot.Api.Contracts;
using MedSlot.Api.Errors;
using MedSlot.Api.RateLimiting;
using MedSlot.Api.Validation;
using MedSlot.Application.Abstractions;
using MedSlot.Application.Identity;

namespace MedSlot.Api.Endpoints;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Вход").AllowAnonymous();

        group.MapPost("/register", Register)
            .WithName("Register")
            .WithSummary("Регистрация пациента")
            .WithDescription("Создаёт карточку пациента и учётную запись, сразу открывает сессию.")
            .Validate<RegisterRequest>()
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .Produces<TokensResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/login", Login)
            .WithName("Login")
            .WithSummary("Вход")
            .Validate<LoginRequest>()
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .Produces<TokensResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refresh", Refresh)
            .WithName("RefreshSession")
            .WithSummary("Обновить сессию")
            .WithDescription(
                "Refresh-токен одноразовый: в ответе новая пара. Повторное предъявление уже использованного " +
                "токена завершает все сессии этого входа (признак утечки).")
            .Validate<RefreshRequest>()
            .Produces<TokensResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", Logout)
            .WithName("Logout")
            .WithSummary("Выйти")
            .WithDescription("Отзывает все refresh-токены этого входа.")
            .Validate<RefreshRequest>()
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static async Task<IResult> Register(
        RegisterRequest request, ICommandHandler<RegisterPatient, AuthTokens> handler, CancellationToken ct) =>
        (await handler.Handle(new RegisterPatient(request.FullName, request.Phone, request.Email, request.Password), ct))
            .ToResponse(tokens => TypedResults.Created((string?)null, TokensResponse.Create(tokens)));

    private static async Task<IResult> Login(LoginRequest request, ICommandHandler<Login, AuthTokens> handler, CancellationToken ct) =>
        (await handler.Handle(new Login(request.Email, request.Password), ct))
            .ToResponse(tokens => TypedResults.Ok(TokensResponse.Create(tokens)));

    private static async Task<IResult> Refresh(
        RefreshRequest request, ICommandHandler<RefreshSession, AuthTokens> handler, CancellationToken ct) =>
        (await handler.Handle(new RefreshSession(request.RefreshToken), ct))
            .ToResponse(tokens => TypedResults.Ok(TokensResponse.Create(tokens)));

    private static async Task<IResult> Logout(RefreshRequest request, ICommandHandler<Logout, Unit> handler, CancellationToken ct) =>
        (await handler.Handle(new Logout(request.RefreshToken), ct)).ToResponse(_ => TypedResults.NoContent());
}
