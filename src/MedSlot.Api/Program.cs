using System.Text.Json.Serialization;
using FluentValidation;
using MedSlot.Api.Auth;
using MedSlot.Api.Endpoints;
using MedSlot.Api.Idempotency;
using MedSlot.Api.OpenApi;
using MedSlot.Api.RateLimiting;
using MedSlot.Application;
using MedSlot.Infrastructure;
using MedSlot.Infrastructure.Idempotency;
using MedSlot.Infrastructure.Outbox;
using MedSlot.Infrastructure.Persistence;
using MedSlot.Infrastructure.Security;
using MedSlot.Infrastructure.Waitlist;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.");

var kafka = new KafkaOptions
{
    BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
        ?? throw new InvalidOperationException("Не задан адрес Kafka: Kafka:BootstrapServers."),
};
var outbox = new OutboxOptions { RelayEnabled = builder.Configuration.GetValue("Outbox:RelayEnabled", true) };
var auth = new AuthOptions
{
    SigningKey = builder.Configuration["Auth:SigningKey"]
        ?? throw new InvalidOperationException("Не задан ключ подписи токенов: Auth:SigningKey."),
};

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddMedSlotOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
builder.Services.AddMedSlotRateLimiting(builder.Configuration);
builder.Services.AddMedSlotAuth();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString, kafka, auth, outbox);
builder.Services.AddHostedService<IdempotencyCleanupService>();
if (builder.Configuration.GetValue("Waitlist:ExpiryEnabled", true))
{
    builder.Services.AddHostedService<WaitlistExpiryService>();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await app.Services.InitializeDatabaseAsync(seedDemoData: true);
    await app.Services.InitializeKafkaTopicsAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapOpenApi();
app.MapScalarApiReference();

// live: процесс жив, внешние зависимости не проверяются.
// ready: готов принимать трафик; сюда попадают проверки с тегом "ready" (Postgres, Kafka).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapAuthEndpoints();
app.MapAppointmentEndpoints();
app.MapSchedulingEndpoints();
app.MapWaitlistEndpoints();
app.MapPatientEndpoints();
app.MapAdminEndpoints();

await app.RunAsync();

// Нужен WebApplicationFactory в интеграционных тестах.
public partial class Program;
