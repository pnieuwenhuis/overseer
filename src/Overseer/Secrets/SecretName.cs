using System.Text.RegularExpressions;

namespace Overseer.Secrets;

/// <summary>
/// Which names a secret may have: Key Vault's rule (1 to 127 letters, digits and dashes), enforced by every
/// backend, so a name that works with the local secrets file also works in Azure.
/// </summary>
public static partial class SecretName
{
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the rule.</exception>
    public static void Validate(string name)
    {
        if (!Valid().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid secret name: use 1 to 127 letters, digits and dashes.", nameof(name));
        }
    }

    [GeneratedRegex("^[0-9A-Za-z-]{1,127}$")]
    private static partial Regex Valid();
}
