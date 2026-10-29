namespace Overseer.Secrets;

/// <summary>
/// Secrets such as the GitHub token, model provider tokens and the SSH keys of Sandboxes, write-only: once set, a
/// secret can be replaced, removed or checked for, but this interface never returns its value. The Portal uses this
/// one; only server-side consumers use <see cref="ISecretReader"/>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Stores the secret, replacing any value it had.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid <see cref="SecretName"/>.</exception>
    Task SetAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Removes the secret, if it is set.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid <see cref="SecretName"/>.</exception>
    Task RemoveAsync(string name, CancellationToken cancellationToken);

    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid <see cref="SecretName"/>.</exception>
    Task<bool> IsSetAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Reads secret values, for server-side consumers only, such as injecting a token into a Sandbox.
/// No Portal page or API may use it: a value, once entered, is never shown again.
/// </summary>
public interface ISecretReader
{
    /// <summary>The secret's value, or <c>null</c> when it is not set.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid <see cref="SecretName"/>.</exception>
    Task<string?> ReadAsync(string name, CancellationToken cancellationToken);
}
