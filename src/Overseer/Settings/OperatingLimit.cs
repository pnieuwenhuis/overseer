using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Overseer.Settings;

/// <summary>One of the operating limits: a whole number of its unit, from 1 up to its maximum.</summary>
/// <param name="Key">Identifies the setting in PostgreSQL.</param>
public sealed record OperatingLimit(string Key, string Name, string Unit, int Default, int Maximum)
{
    public static readonly OperatingLimit MaxActiveIntents = new("OperatingLimits:MaxActiveIntents", "Maximum Active Intents", "Intents", 2, 10);
    public static readonly OperatingLimit RefinementIdleTimeout = new("OperatingLimits:RefinementIdleTimeoutMinutes", "Refinement idle timeout", "minutes", 30, 480);
    public static readonly OperatingLimit VerificationAttemptsPerTask = new("OperatingLimits:VerificationAttemptsPerTask", "Verification attempts per Task", "attempts", 3, 10);
    public static readonly OperatingLimit MaxSandboxLifetime = new("OperatingLimits:MaxSandboxLifetimeHours", "Maximum Sandbox lifetime", "hours", 12, 48);

    /// <summary>Every operating limit, in the order the Settings page shows them.</summary>
    public static IReadOnlyList<OperatingLimit> All { get; } = [MaxActiveIntents, RefinementIdleTimeout, VerificationAttemptsPerTask, MaxSandboxLifetime];

    /// <summary>Reads a value as the user entered it.</summary>
    /// <param name="error">Why <paramref name="input"/> is not a valid value of this limit.</param>
    public bool TryParse(string? input, out int value, [NotNullWhen(false)] out string? error)
    {
        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= Maximum)
        {
            error = null;
            return true;
        }
        error = $"{Name} must be a whole number from 1 to {Maximum}.";
        return false;
    }
}

/// <summary>The current operating limits, as the modules that enforce them use them.</summary>
public sealed record OperatingLimits(int MaxActiveIntents, TimeSpan RefinementIdleTimeout, int VerificationAttemptsPerTask, TimeSpan MaxSandboxLifetime);
