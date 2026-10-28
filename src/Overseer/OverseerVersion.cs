using System.Reflection;

namespace Overseer;

public static class OverseerVersion
{
    /// <summary>The version of every local build (see the project file); no release or image has it.</summary>
    public const string Development = "0.0.0-dev";

    /// <summary>The version stamped into this executable at build time.</summary>
    public static string Current { get; } =
        typeof(OverseerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
