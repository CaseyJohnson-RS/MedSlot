using System.Reflection;

namespace MedSlot.Workers.Audit;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
