using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Overseer.Secrets;

namespace Overseer.ModelProviders;

/// <summary>
/// A model provider token: a key for a model provider, such as Azure Foundry or an AI gateway, stored under a name. Every
/// agent session gets it as the environment variable its name maps to, which the agent harness configuration references.
/// </summary>
/// <param name="Name">Lower-case letters, digits and dashes, starting with a letter, such as <c>azure-foundry</c>.</param>
public sealed partial record ModelProviderToken(string Name)
{
    /// <summary>What the names of the secrets that hold provider tokens start with.</summary>
    public const string SecretPrefix = "model-provider-token-";

    private const int MaxNameLength = 64;

    /// <summary>
    /// The environment variable agent sessions get the token in: the name in capitals, dashes as underscores, followed by
    /// <c>_API_KEY</c>. So <c>anthropic</c> gives <c>ANTHROPIC_API_KEY</c>, the variable the provider reads by default.
    /// </summary>
    public string EnvironmentVariable => $"{Name.ToUpperInvariant().Replace('-', '_')}_API_KEY";

    /// <summary>The name of the secret that holds the token.</summary>
    public string SecretName => SecretPrefix + Name;

    /// <summary>Reads a token's name as the user entered it, ignoring case and surrounding spaces.</summary>
    public static bool TryParse(string? name, [NotNullWhen(true)] out ModelProviderToken? token, [NotNullWhen(false)] out string? error)
    {
        name = name?.Trim().ToLowerInvariant() ?? "";
        token = null;
        if (name == "")
        {
            error = "Enter a name for the token, such as azure-foundry.";
            return false;
        }
        if (name.Length > MaxNameLength || !ValidName().IsMatch(name))
        {
            error = $"A token's name is 1 to {MaxNameLength} letters, digits and dashes, starting with a letter, such as azure-foundry.";
            return false;
        }
        token = new ModelProviderToken(name);
        error = null;
        return true;
    }

    /// <summary>The token stored in the secret named <paramref name="secretName"/>; <c>null</c> when it isn't a provider token's.</summary>
    public static ModelProviderToken? FromSecretName(string secretName) =>
        secretName.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase)
        && TryParse(secretName[SecretPrefix.Length..], out var token, out _)
            ? token
            : null;

    /// <summary>
    /// Whether <paramref name="value"/> can be a token: printable ASCII without spaces, as provider keys are. This also
    /// keeps line breaks out of the environment variable.
    /// </summary>
    public static bool IsWellFormedValue(string value) => value.Length > 0 && value.All(character => character is > ' ' and < '\u007f');

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex ValidName();
}

/// <summary>
/// The model provider tokens as the user adds, replaces and removes them on the Settings page, write-only like every
/// secret: they are listed by name, never by value.
/// </summary>
public sealed class ModelProviderTokens(ISecretStore secrets)
{
    /// <summary>The stored tokens, by name.</summary>
    public async Task<IReadOnlyList<ModelProviderToken>> ListAsync(CancellationToken cancellationToken)
    {
        var names = await secrets.ListAsync(ModelProviderToken.SecretPrefix, cancellationToken);
        return [.. names.Select(ModelProviderToken.FromSecretName).OfType<ModelProviderToken>().Distinct().OrderBy(token => token.Name, StringComparer.Ordinal)];
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="name"/>, replacing the token stored under it, if any.</summary>
    public async Task<ModelProviderTokenSaving> SaveAsync(string? name, string? value, CancellationToken cancellationToken)
    {
        if (!ModelProviderToken.TryParse(name, out var token, out var error))
        {
            return new ModelProviderTokenSaving.Refused(error);
        }
        value = value?.Trim() ?? "";
        if (value == "")
        {
            return new ModelProviderTokenSaving.Refused("Enter the token.");
        }
        if (!ModelProviderToken.IsWellFormedValue(value))
        {
            return new ModelProviderTokenSaving.Refused("This is not a token: a token has no spaces, line breaks or other special characters.");
        }

        var replaced = await secrets.IsSetAsync(token.SecretName, cancellationToken);
        await secrets.SetAsync(token.SecretName, value, cancellationToken);
        return new ModelProviderTokenSaving.Stored(token, replaced);
    }

    /// <summary>Removes the token stored under <paramref name="name"/>.</summary>
    /// <returns>The removed token; <c>null</c> when no token is stored under that name.</returns>
    public async Task<ModelProviderToken?> RemoveAsync(string? name, CancellationToken cancellationToken)
    {
        if (!ModelProviderToken.TryParse(name, out var token, out _) || !await secrets.IsSetAsync(token.SecretName, cancellationToken))
        {
            return null;
        }
        await secrets.RemoveAsync(token.SecretName, cancellationToken);
        return token;
    }
}

/// <summary>What saving a model provider token did.</summary>
public abstract record ModelProviderTokenSaving
{
    private ModelProviderTokenSaving()
    {
    }

    /// <param name="Replaced">Whether a token was stored under this name before.</param>
    public sealed record Stored(ModelProviderToken Token, bool Replaced) : ModelProviderTokenSaving;

    /// <summary>The name or the token can't be used, so nothing was stored.</summary>
    public sealed record Refused(string Error) : ModelProviderTokenSaving;
}
