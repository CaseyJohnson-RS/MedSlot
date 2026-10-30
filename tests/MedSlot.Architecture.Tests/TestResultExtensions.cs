using TestResult = NetArchTest.Rules.TestResult;

namespace MedSlot.Architecture.Tests;

internal static class TestResultExtensions
{
    public static void ShouldBeSuccessful(this TestResult result, string? context = null)
    {
        var failing = result.FailingTypeNames ?? [];
        var prefix = context is null ? string.Empty : $"[{context}] ";

        result.IsSuccessful.ShouldBeTrue($"{prefix}Нарушают правило: {string.Join(", ", failing)}");
    }
}
