using System.Reflection;

namespace MedSlot.Workers.Notifications;

public static class AssemblyReference
{
    public static readonly Assembly Assembly = typeof(AssemblyReference).Assembly;
}
