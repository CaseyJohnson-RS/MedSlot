using System.Reflection;

namespace MedSlot.Architecture.Tests;

internal static class Assemblies
{
    public static readonly Assembly Domain = MedSlot.Domain.AssemblyReference.Assembly;
    public static readonly Assembly Application = MedSlot.Application.AssemblyReference.Assembly;
    public static readonly Assembly Infrastructure = MedSlot.Infrastructure.AssemblyReference.Assembly;
    public static readonly Assembly Api = typeof(Program).Assembly;

    public static readonly Assembly[] Workers =
    [
        MedSlot.Workers.Notifications.AssemblyReference.Assembly,
        MedSlot.Workers.Analytics.AssemblyReference.Assembly,
        MedSlot.Workers.Audit.AssemblyReference.Assembly,
    ];
}

internal static class Namespaces
{
    public const string Domain = "MedSlot.Domain";
    public const string Application = "MedSlot.Application";
    public const string Infrastructure = "MedSlot.Infrastructure";
    public const string Api = "MedSlot.Api";
    public const string Workers = "MedSlot.Workers";

    public const string AspNetCore = "Microsoft.AspNetCore";
    public const string EntityFrameworkCore = "Microsoft.EntityFrameworkCore";
    public const string Npgsql = "Npgsql";
    public const string Kafka = "Confluent.Kafka";
}
