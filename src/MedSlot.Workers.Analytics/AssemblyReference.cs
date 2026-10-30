using System.Reflection;

namespace MedSlot.Workers.Analytics;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
