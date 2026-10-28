using System.Text.Json;

namespace Overseer.Secrets;

/// <summary>
/// Secrets in a local JSON file that only the user may read, for running Overseer without Key Vault.
/// Every call reads the file afresh, so a secret set by another process is seen straight away.
/// </summary>
public sealed class FileSecretStore(string path) : ISecretStore, ISecretReader
{
    private readonly SemaphoreSlim _writing = new(1, 1);

    public string Path { get; } = path;

    public Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        SecretName.Validate(name);
        return ChangeAsync(secrets => secrets[name] = value, cancellationToken);
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        SecretName.Validate(name);
        return ChangeAsync(secrets => secrets.Remove(name), cancellationToken);
    }

    public async Task<bool> IsSetAsync(string name, CancellationToken cancellationToken)
    {
        SecretName.Validate(name);
        return (await LoadAsync(cancellationToken)).ContainsKey(name);
    }

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        SecretName.Validate(name);
        return (await LoadAsync(cancellationToken)).GetValueOrDefault(name);
    }

    private async Task ChangeAsync(Action<Dictionary<string, string>> change, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            var secrets = await LoadAsync(cancellationToken);
            change(secrets);
            await SaveAsync(secrets, cancellationToken);
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        // Key Vault's names are case-insensitive, so these are too.
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Path))
        {
            await using var file = File.OpenRead(Path);
            foreach (var (name, value) in await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(file, cancellationToken: cancellationToken) ?? [])
            {
                secrets[name] = value;
            }
        }
        return secrets;
    }

    /// <summary>Writes a new file and moves it into place, so a reader never sees half a file.</summary>
    private async Task SaveAsync(Dictionary<string, string> secrets, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var newFile = $"{Path}.{Guid.NewGuid():N}.tmp";
        // Only the user may read the file: it holds the secrets in plain text.
        await using (var file = UserOnlyFile.CreateNew(newFile))
        {
            await JsonSerializer.SerializeAsync(file, secrets, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        }
        File.Move(newFile, Path, overwrite: true);
    }
}
