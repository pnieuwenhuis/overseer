using Overseer.Secrets;

namespace Overseer.Tests;

/// <summary>
/// What every secret store backend must do. Derive from this once per backend. Each test uses fresh secret names
/// and removes them afterwards, so the suite can share a real vault with others.
/// </summary>
public abstract class SecretStoreContract<TBackend> : IAsyncDisposable where TBackend : ISecretStore, ISecretReader
{
    private readonly List<string> _names = [];

    /// <summary>The backend under test, through both its write-only and its server-side interface.</summary>
    protected abstract TBackend Backend { get; }

    private ISecretStore Store => Backend;

    private ISecretReader Reader => Backend;

    [Fact]
    public async Task A_set_secret_is_present_and_readable_server_side()
    {
        var name = NewName();

        await Store.SetAsync(name, "github_pat_11ABC", TestContext.Current.CancellationToken);

        Assert.True(await Store.IsSetAsync(name, TestContext.Current.CancellationToken));
        Assert.Equal("github_pat_11ABC", await Reader.ReadAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Setting_a_secret_again_replaces_its_value()
    {
        var name = NewName();
        await Store.SetAsync(name, "github_pat_old", TestContext.Current.CancellationToken);

        await Store.SetAsync(name, "github_pat_new", TestContext.Current.CancellationToken);

        Assert.Equal("github_pat_new", await Reader.ReadAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_removed_secret_is_gone_and_can_be_set_again()
    {
        var name = NewName();
        var kept = NewName();
        await Store.SetAsync(name, "github_pat_11ABC", TestContext.Current.CancellationToken);
        await Store.SetAsync(kept, "sk-provider", TestContext.Current.CancellationToken);

        await Store.RemoveAsync(name, TestContext.Current.CancellationToken);

        Assert.False(await Store.IsSetAsync(name, TestContext.Current.CancellationToken));
        Assert.Null(await Reader.ReadAsync(name, TestContext.Current.CancellationToken));
        Assert.True(await Store.IsSetAsync(kept, TestContext.Current.CancellationToken));
        await Store.SetAsync(name, "github_pat_22DEF", TestContext.Current.CancellationToken);
        Assert.Equal("github_pat_22DEF", await Reader.ReadAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_secret_that_was_never_set_is_absent_and_removing_it_does_nothing()
    {
        var name = NewName();

        await Store.RemoveAsync(name, TestContext.Current.CancellationToken);

        Assert.False(await Store.IsSetAsync(name, TestContext.Current.CancellationToken));
        Assert.Null(await Reader.ReadAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Names_that_differ_only_in_case_are_the_same_secret()
    {
        var name = NewName();
        await Store.SetAsync(name, "github_pat_old", TestContext.Current.CancellationToken);

        await Store.SetAsync(name.ToUpperInvariant(), "github_pat_new", TestContext.Current.CancellationToken);

        Assert.Equal("github_pat_new", await Reader.ReadAsync(name, TestContext.Current.CancellationToken));
        await Store.RemoveAsync(name.ToUpperInvariant(), TestContext.Current.CancellationToken);
        Assert.False(await Store.IsSetAsync(name, TestContext.Current.CancellationToken));
    }

    // Key Vault's rule, enforced by every backend, so a name that works locally also works in Azure.
    [Theory]
    [InlineData("")]
    [InlineData("github_token")]
    [InlineData("github token")]
    [InlineData("../config")]
    public async Task A_name_other_than_letters_digits_and_dashes_is_refused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SetAsync(name, "github_pat_11ABC", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.IsSetAsync(name, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.RemoveAsync(name, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ReadAsync(name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_name_longer_than_127_characters_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store.SetAsync(new string('a', 128), "github_pat_11ABC", TestContext.Current.CancellationToken));
    }

    private string NewName()
    {
        var name = $"test-{Guid.NewGuid():N}";
        _names.Add(name);
        return name;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _names)
        {
            await Store.RemoveAsync(name, CancellationToken.None);
        }
        await DisposeBackendAsync();
        GC.SuppressFinalize(this);
    }

    protected virtual ValueTask DisposeBackendAsync() => ValueTask.CompletedTask;
}

public sealed class FileSecretStoreContract : SecretStoreContract<FileSecretStore>
{
    private readonly string _directory = Directory.CreateTempSubdirectory("overseer-secrets-").FullName;
    private readonly FileSecretStore _store;

    public FileSecretStoreContract() => _store = new FileSecretStore(Path.Combine(_directory, "secrets.json"));

    protected override FileSecretStore Backend => _store;

    protected override ValueTask DisposeBackendAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Opt-in: runs against a real vault when <c>TEST_KEYVAULT_URI</c> is set, through your Azure CLI login,
/// which needs the Key Vault Secrets Officer role on that vault.
/// </summary>
public sealed class KeyVaultSecretStoreContract : SecretStoreContract<KeyVaultSecretStore>
{
    private const string VaultVariable = "TEST_KEYVAULT_URI";

    private readonly KeyVaultSecretStore _store;

    public KeyVaultSecretStoreContract()
    {
        var vault = Environment.GetEnvironmentVariable(VaultVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(vault), $"Set {VaultVariable} to a Key Vault URI to run the Key Vault contract.");
        _store = KeyVaultSecretStore.Connect(new Uri(vault), managedIdentityClientId: null);
    }

    protected override KeyVaultSecretStore Backend => _store;
}
