using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;

namespace Overseer.Settings;

/// <summary>
/// Overseer's Settings, kept in PostgreSQL; secrets are not settings and stay in the secret store. Every read goes to the
/// database, so a saved change applies without a restart. A setting that was never saved has its default.
/// </summary>
public sealed class SettingsStore(IDbContextFactory<OverseerDbContext> databases)
{
    public async Task<OperatingLimits> GetOperatingLimitsAsync(CancellationToken cancellationToken)
    {
        var values = await GetOperatingLimitValuesAsync(cancellationToken);
        return new OperatingLimits(
            values[OperatingLimit.MaxActiveIntents],
            TimeSpan.FromMinutes(values[OperatingLimit.RefinementIdleTimeout]),
            values[OperatingLimit.VerificationAttemptsPerTask],
            TimeSpan.FromHours(values[OperatingLimit.MaxSandboxLifetime]));
    }

    /// <summary>The current value of each operating limit, in its unit; its default if the saved value is no longer valid.</summary>
    public async Task<IReadOnlyDictionary<OperatingLimit, int>> GetOperatingLimitValuesAsync(CancellationToken cancellationToken)
    {
        var keys = OperatingLimit.All.Select(limit => limit.Key).ToList();
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var saved = await database.Settings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

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

        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (limit, value) in values)
        {
            // An upsert, so two first saves at once can't both insert the same setting.
            var text = value.ToString(CultureInfo.InvariantCulture);
            await database.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Settings" ("Key", "Value") VALUES ({limit.Key}, {text}) ON CONFLICT ("Key") DO UPDATE SET "Value" = excluded."Value" """,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return errors;
    }
}

/// <summary>A saved setting, as PostgreSQL holds it.</summary>
public sealed class SettingRecord
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}
