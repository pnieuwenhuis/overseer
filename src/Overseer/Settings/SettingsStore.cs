using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Settings;

/// <summary>
/// Overseer's Settings, kept in PostgreSQL; secrets are not settings and stay in the secret store. Every read goes to the
/// database, so a saved change applies without a restart. A setting that was never saved has its default.
/// </summary>
public sealed class SettingsStore(IDbContextFactory<OverseerDbContext> databases)
{
    /// <summary>
    /// New operating limits were saved: raised once they are committed, for what waits on a limit, such as an idle Refinement on its
    /// timeout, to read them again. Handlers must not throw.
    /// </summary>
    public event Action? OperatingLimitsSaved;

    public async Task<OperatingLimits> GetOperatingLimitsAsync(CancellationToken cancellationToken)
    {
        var values = await GetOperatingLimitValuesAsync(cancellationToken);
        return new OperatingLimits(
            values[OperatingLimit.MaxActiveIntents],
            TimeSpan.FromMinutes(values[OperatingLimit.RefinementIdleTimeout]),
            values[OperatingLimit.VerificationAttemptsPerTask],
            TimeSpan.FromHours(values[OperatingLimit.MaxSandboxLifetime]));
    }

    private const string SandboxVmSizeKey = "Sandboxes:VmSize";
    private const string SandboxSpotKey = "Sandboxes:Spot";

    /// <summary>The machine the next Sandbox is created on; the default for what was never saved or is no longer valid.</summary>
    public async Task<SandboxMachine> GetSandboxMachineAsync(CancellationToken cancellationToken)
    {
        var saved = await ReadAsync([SandboxVmSizeKey, SandboxSpotKey], cancellationToken);
        return new SandboxMachine(
            saved.TryGetValue(SandboxVmSizeKey, out var size) && SandboxMachine.IsVmSize(size) ? size : SandboxMachine.Default.VmSize,
            saved.TryGetValue(SandboxSpotKey, out var text) && bool.TryParse(text, out var spot) ? spot : SandboxMachine.Default.Spot);
    }

    /// <summary>Saves the machine later Sandboxes are created on, as the user chose it; nothing if the VM size isn't one of the choices.</summary>
    /// <returns>Why the choice was rejected; null when it was saved.</returns>
    public async Task<string?> SaveSandboxMachineAsync(string? vmSize, bool spot, CancellationToken cancellationToken)
    {
        if (!SandboxMachine.IsVmSize(vmSize))
        {
            return $"The VM size must be one of {string.Join(" and ", SandboxMachine.VmSizes.Select(size => size.Name))}.";
        }
        await SaveAsync(new Dictionary<string, string>
        {
            [SandboxVmSizeKey] = vmSize,
            [SandboxSpotKey] = spot ? "true" : "false",
        }, cancellationToken);
        return null;
    }

    private const string ReviewGuidelinesKey = "Review:Guidelines";

    /// <summary>The Review Guidelines as Markdown, which the Reviewer receives with every review; empty while none were saved.</summary>
    public async Task<string> GetReviewGuidelinesAsync(CancellationToken cancellationToken)
    {
        var saved = await ReadAsync([ReviewGuidelinesKey], cancellationToken);
        return saved.GetValueOrDefault(ReviewGuidelinesKey, "");
    }

    /// <summary>Saves the Review Guidelines as the user wrote them, kept as <see cref="ReviewGuidelines.FromEditor"/> says.</summary>
    public Task SaveReviewGuidelinesAsync(string? guidelines, CancellationToken cancellationToken) =>
        SaveAsync(new Dictionary<string, string> { [ReviewGuidelinesKey] = ReviewGuidelines.FromEditor(guidelines) }, cancellationToken);

    private const string AgentHarnessKey = "Agents:Harness";

    /// <summary>
    /// The agent harness configuration agent sessions start with now; Overseer's default while none was saved, or if the
    /// saved one is no longer valid.
    /// </summary>
    public async Task<AgentHarnessConfiguration> GetAgentHarnessAsync(CancellationToken cancellationToken)
    {
        var saved = await ReadAsync([AgentHarnessKey], cancellationToken);
        return saved.TryGetValue(AgentHarnessKey, out var json) && AgentHarnessConfiguration.TryParse(json, out var harness, out _)
            ? harness
            : AgentHarnessConfiguration.Default;
    }

    /// <summary>Saves the agent harness configuration as the user wrote it; nothing if it isn't valid.</summary>
    /// <returns>Why the configuration was rejected; null when it was saved.</returns>
    public async Task<string?> SaveAgentHarnessAsync(string? json, CancellationToken cancellationToken)
    {
        if (!AgentHarnessConfiguration.TryParse(json, out var harness, out var error))
        {
            return error;
        }
        await SaveAsync(new Dictionary<string, string> { [AgentHarnessKey] = harness.Json }, cancellationToken);
        return null;
    }

    /// <summary>Goes back to Overseer's default agent harness configuration, including the changes later versions make to it.</summary>
    public Task RestoreDefaultAgentHarnessAsync(CancellationToken cancellationToken) => RemoveAsync(AgentHarnessKey, cancellationToken);

    /// <summary>Changes the model of each role in the current agent harness configuration and saves it; nothing if a model is empty.</summary>
    /// <returns>Why the models were rejected; null when they were saved.</returns>
    public async Task<string?> SaveAgentModelsAsync(IReadOnlyDictionary<AgentRole, string?> models, CancellationToken cancellationToken)
    {
        var current = await GetAgentHarnessAsync(cancellationToken);
        return await SaveAgentHarnessAsync(current.WithModels(models), cancellationToken);
    }

    /// <summary>The current value of each operating limit, in its unit; its default if the saved value is no longer valid.</summary>
    public async Task<IReadOnlyDictionary<OperatingLimit, int>> GetOperatingLimitValuesAsync(CancellationToken cancellationToken)
    {
        var saved = await ReadAsync([.. OperatingLimit.All.Select(limit => limit.Key)], cancellationToken);

        return OperatingLimit.All.ToDictionary(
            limit => limit,
            limit => saved.TryGetValue(limit.Key, out var text) && limit.TryParse(text, out var value, out _) ? value : limit.Default);
    }

    /// <summary>Saves the operating limits as the user entered them, in their units; if any is invalid, none is saved.</summary>
    /// <returns>Why each invalid value was rejected; empty when they were saved.</returns>
    public async Task<IReadOnlyDictionary<OperatingLimit, string>> SaveOperatingLimitsAsync(
        IReadOnlyDictionary<OperatingLimit, string?> input, CancellationToken cancellationToken)
    {
        var values = new Dictionary<OperatingLimit, int>();
        var errors = new Dictionary<OperatingLimit, string>();
        foreach (var (limit, text) in input)
        {
            if (limit.TryParse(text, out var value, out var error))
            {
                values[limit] = value;
            }
            else
            {
                errors[limit] = error;
            }
        }
        if (errors.Count > 0)
        {
            return errors;
        }

        await SaveAsync(
            values.ToDictionary(pair => pair.Key.Key, pair => pair.Value.ToString(CultureInfo.InvariantCulture)), cancellationToken);
        OperatingLimitsSaved?.Invoke();
        return errors;
    }

    /// <summary>The saved value of each of the settings that has one.</summary>
    private async Task<Dictionary<string, string>> ReadAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        return await database.Settings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);
    }

    /// <summary>Forgets a setting's saved value, so it has its default again.</summary>
    private async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await database.Settings.Where(setting => setting.Key == key).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Saves the settings together, in one transaction.</summary>
    private async Task SaveAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (key, value) in settings)
        {
            // An upsert, so two first saves at once can't both insert the same setting.
            await database.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Settings" ("Key", "Value") VALUES ({key}, {value}) ON CONFLICT ("Key") DO UPDATE SET "Value" = excluded."Value" """,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>A saved setting, as PostgreSQL holds it.</summary>
public sealed class SettingRecord
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}
