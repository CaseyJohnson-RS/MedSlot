using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference();

// live: процесс жив, внешние зависимости не проверяются.
// ready: готов принимать трафик; сюда попадают проверки с тегом "ready" (Postgres, Kafka — с фазы 2).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

// Нужен WebApplicationFactory в интеграционных тестах.
public partial class Program;
