using System.Reflection;

namespace Overseer;

public static class OverseerVersion
{
    /// <summary>The version stamped into this executable at build time.</summary>
    public static string Current { get; } =
        typeof(OverseerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
