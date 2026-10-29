# Overseer

Overseer lets one person direct a team of AI agents, installed into their own Azure subscription. Its Portal lets you capture Intents, refine them into Specs with an Analyst agent, have a Squad of agents implement them in Sandboxes, and review and merge the result. See [the v1 spec](docs/specs/overseer-v1.md), the [glossary](GLOSSARY.md) and the [ADRs](docs/adr/).

Overseer is a single `overseer` executable (ADR 0004). `overseer serve` runs the Portal and the orchestration; `install`, `uninstall` and `reset-password` manage the Azure deployment. Run `overseer` without a command to list them, and `overseer --version` for its version.

## Installing Overseer in Azure

You need the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), logged in (`az login`) to the subscription to install into, as Owner of it (or Contributor plus User Access Administrator: install creates a role assignment). Then run a released `overseer`:

```sh
overseer install [--region westeurope] [--prefix overseer]
```

Install shows the subscription it uses (`az account set` picks another), then creates everything, which takes 15 to 25 minutes, and prints the Portal URL, the username and a freshly generated password. Only the password's hash leaves your machine, as a Container App secret (ADR 0005); the password is shown only then.

`--prefix` (3 to 12 lowercase letters, digits and hyphens) starts every resource name and identifies the installation. It creates:

- **`<prefix>-rg`**: a Container Apps environment (`<prefix>-env`, logging to `<prefix>-logs`) on the VNet `<prefix>-vnet`; the Container App `<prefix>`, one replica of `ghcr.io/pnieuwenhuis/overseer` at the CLI's own version; PostgreSQL Flexible Server `<prefix>-db-<suffix>` (Burstable, private on the VNet, Microsoft Entra authentication only); Key Vault `<prefix>-kv-<suffix>`; and the user-assigned Managed Identity `<prefix>-identity`. Key Vault and PostgreSQL names must be unique across Azure, so `<suffix>` is derived from the subscription ID: the same prefix always gives the same names.
- **`<prefix>-sandboxes-rg`**, for Sandboxes.

The Managed Identity's only Azure role assignment is Contributor on the Sandbox resource group. Its other access is data-plane only: a Key Vault access policy for secrets (the vault uses access policies instead of Azure RBAC for exactly this reason), and being the only PostgreSQL login, as the server's Entra administrator. The application logs in to PostgreSQL with a token for the identity, so no database password exists.

The scripts install runs are embedded in the executable (`src/Overseer/Installer/Scripts`) and manage Azure only through the Azure CLI. A failing step stops install with the Azure error and, for missing permissions, quotas and unavailable regions, what to do about it. A development build refuses to install, because no image is published for `0.0.0-dev`.

### Upgrading

To upgrade, get the new `overseer` and run `install` again with the same `--prefix`. Install finds the existing installation by its resource group `<prefix>-rg`, tagged `overseer-prefix=<prefix>`. It stops rather than take over a `<prefix>-rg` or `<prefix>-sandboxes-rg` without that tag. It:

- keeps it in its region, so `--region` can be left out. Naming another region is an error, because an installation can't move;
- creates only what is missing, so re-running after an install that stopped partway completes it, and re-running the same version changes nothing;
- updates the Container App to the image of the CLI's version, with the same settings, and waits for the new revision; the server applies new migrations when it starts;
- leaves the database, the Key Vault with its secrets, and the password alone. It prints the Portal URL and the username, and says the password is kept: `overseer reset-password` sets a new one;
- refuses to deploy a version older than the one running, because migrations only go forward.

### Resetting the password

If you lose the password, generate a new one for the installation with the same `--prefix` as install:

```sh
overseer reset-password [--prefix overseer]
```

It finds the installation in the subscription your Azure CLI uses, sets the new password's hash as the Container App's `password-hash` secret, restarts the active revision and waits until its new replica is ready and the old one, which still has the old hash, is gone. Then it prints the username and the new password, once. The old password no longer works, and sessions logged in with it have ended (ADR 0005). Nothing else changes: no new revision, no other setting, and the database and Key Vault are left alone. It needs Contributor on `<prefix>-rg`.

If the restart fails, it still prints the password, with the `az` command to restart the Container App yourself: the hash is set, and the next restart makes it the only one that works. Running reset-password again would replace it.

### Uninstalling

To remove an installation, and stop paying for it, run uninstall with the same `--prefix` as install:

```sh
overseer uninstall [--prefix overseer] [--yes]
```

It finds the installation in the subscription your Azure CLI uses and lists what it removes. Only after you type the prefix (or with `--yes`, for scripts) does it remove it:

1. the Managed Identity's role assignments, so the running Overseer loses its rights on the Sandbox resource group first, and none are left orphaned;
2. `<prefix>-sandboxes-rg`, with every Sandbox still in it;
3. `<prefix>-rg`, with the Portal, the database and the Key Vault with its secrets. This takes 10 to 20 minutes. Its Log Analytics workspace is deleted for good first: deleting the group would only soft-delete it, and a later install would recover the old logs;
4. the soft-deleted Key Vault, purged, so that a later install with the same prefix can create it again.

This can't be undone. It removes only resource groups tagged `overseer-prefix=<prefix>`: a group of the same name without that tag isn't Overseer's, and uninstall says so and leaves it alone. With nothing installed it says there is nothing to remove, and succeeds. A failing step stops it with the Azure error; running it again removes what is left. It needs Owner on the subscription, or Contributor plus User Access Administrator, as install does.

## Running Overseer locally

You need the .NET 10 SDK and Docker.

1. Start PostgreSQL:

   ```sh
   docker compose up -d
   ```

   This starts PostgreSQL on host port **5432**, with a named volume so data survives restarts. `docker compose down -v` throws the data away.

2. Generate a password:

   ```sh
   dotnet run --project src/Overseer -- reset-password --local
   ```

   This prints the username and a new password, once, and writes only the password's hash to the local config file (see [Configuration](#configuration)). Run it again whenever you lose the password, then restart the server. Without a hash, `serve` refuses to start (ADR 0005).

3. Run the server:

   ```sh
   dotnet run --project src/Overseer
   ```

   The `serve` launch profile runs `overseer serve` in the Development environment, which points at the compose database. Open <http://localhost:5080> and log in. The Portal shows the running version in its header.

Schema migrations are applied at startup, so an empty database gets the schema on the first run and later runs change nothing.

## Configuration

Settings are layered. Later layers override earlier ones:

1. **Defaults**: `appsettings.json` next to the executable, plus `appsettings.Development.json` in the Development environment.
2. **Local config file**: `~/.config/overseer/config.json` (`$XDG_CONFIG_HOME/overseer/config.json` when that is set). Set `OVERSEER_CONFIG_FILE` to use another path.
3. **Environment variables** prefixed with `OVERSEER_`, with `__` separating sections.

Other environment variables aren't read as settings. The only exception is `ASPNETCORE_ENVIRONMENT`, which picks the environment (Production when unset).

| Setting          | Config file key                   | Environment variable                        | Default                                                                                     |
| ---------------- | --------------------------------- | ------------------------------------------- | ------------------------------------------------------------------------------------------- |
| Database         | `ConnectionStrings:Overseer`      | `OVERSEER_CONNECTIONSTRINGS__OVERSEER`      | none; `serve` exits with a message when it is missing (the compose database in Development) |
| Listen address   | `Urls`                            | `OVERSEER_URLS`                             | `http://localhost:5080`                                                                     |
| Username         | `User:Name`                       | `OVERSEER_USER__NAME`                       | `overseer`                                                                                  |
| Password hash    | `User:PasswordHash`               | `OVERSEER_USER__PASSWORDHASH`               | none; `serve` exits with a message pointing at `reset-password --local`                     |
| Database login   | `Database:EntraAuthentication`    | `OVERSEER_DATABASE__ENTRAAUTHENTICATION`    | `false`: the connection string's password. `true`: a Microsoft Entra token, as in Azure (see [Database login](#database-login)) |
| Database identity | `Database:ManagedIdentityClientId` | `OVERSEER_DATABASE__MANAGEDIDENTITYCLIENTID` | none; the system-assigned identity, or your Azure CLI login                                |
| Azure provider   | `SandboxProvider:Azure`           | `OVERSEER_SANDBOXPROVIDER__AZURE__*`        | none; the fake Sandbox Provider (see [Sandboxes](#sandboxes))                               |
| Key Vault        | `Secrets:KeyVaultUri`             | `OVERSEER_SECRETS__KEYVAULTURI`             | none; the local secrets file (see [Secrets](#secrets))                                      |
| Managed Identity | `Secrets:ManagedIdentityClientId` | `OVERSEER_SECRETS__MANAGEDIDENTITYCLIENTID` | none; the system-assigned identity, or your Azure CLI login                                 |
| Secrets file     | `Secrets:File`                    | `OVERSEER_SECRETS__FILE`                    | `secrets.json` next to the local config file: `~/.config/overseer/secrets.json`             |

For example, to listen on another port for one run:

```sh
OVERSEER_URLS=http://localhost:5090 dotnet run --project src/Overseer
```

### Database login

With `Database:EntraAuthentication` set to `true`, `serve` logs in to PostgreSQL as the connection string's `Username` with a Microsoft Entra token instead of a password, renewed every 30 minutes. In Azure the token is the Managed Identity's (`Database:ManagedIdentityClientId` names a user-assigned one); locally it is your Azure CLI login's. Install configures this, so the deployed connection string holds no password.

### Sandboxes

Without `SandboxProvider:Azure` settings, `serve` uses the fake Sandbox Provider and logs that at startup. Its Sandboxes exist only in memory, every command succeeds without output, and every agent turn is done straight away, so the Portal and orchestration work offline. Tests script it through `FakeSandboxProvider` (command results in sequence, agent sessions turn by turn with their updates and outcomes, creation failures, slow provisioning). The Azure provider isn't built yet (ticket 10), so configuring `SandboxProvider:Azure` makes `serve` refuse to start.

Any Sandbox Provider must pass the contract suite in `tests/Overseer.Tests/SandboxProviderContract.cs`: derive from it once per implementation.

### Secrets

Secrets, such as the GitHub token and model provider tokens, are write-only: once set, a secret can be replaced, removed or checked for, but the Portal never shows its value again. The Portal uses `ISecretStore`, which has no way to read a value; only server-side code, such as injecting a token into a Sandbox, uses `ISecretReader`. A test fails if a Portal component injects the reader. Secret values never go into PostgreSQL or the logs; the logs only say which secret was set or removed. Names are 1 to 127 letters, digits and dashes (Key Vault's rule) and ignore case, whichever backend holds them.

`serve` logs at startup where secrets are kept:

- **Key Vault**, when `Secrets:KeyVaultUri` is set. In Azure, Overseer reaches the vault through its Managed Identity (set `Secrets:ManagedIdentityClientId` for a user-assigned one). Locally, it falls back to your Azure CLI login (`az login`), which needs the Key Vault Secrets Officer role on the vault. Removing a secret soft-deletes it, as Key Vault always does; setting it again recovers it and replaces its value, so this also works with purge protection.
- **The local secrets file** otherwise: `secrets.json` next to the local config file, so `~/.config/overseer/secrets.json` by default, outside any repository. Overseer creates it readable only by you. It holds the values in plain text, so don't move it into a repository. `Secrets:File` puts it elsewhere.

Every backend must pass the contract suite in `tests/Overseer.Tests/SecretStoreContract.cs`. The file backend runs in the default test run. The Key Vault backend runs only when `TEST_KEYVAULT_URI` points at a vault where your Azure CLI login has Key Vault Secrets Officer; each test uses fresh secret names and removes them afterwards:

```sh
TEST_KEYVAULT_URI=https://my-vault.vault.azure.net/ dotnet test --filter KeyVaultSecretStoreContract
```

## Tests

```sh
dotnet test
```

Docker must be running: the server tests start Overseer in-process against a real PostgreSQL through Testcontainers.

The install, uninstall and Azure reset-password tests run `overseer install`, `overseer uninstall` and `overseer reset-password` against a fake `az` (`tests/Overseer.Tests/FakeAzureCli.cs`), so they need bash but no Azure. An opt-in smoke test installs for real into the subscription your Azure CLI is logged in to, with a fresh prefix, logs in to the Portal, resets the password (after which only the new one logs in), runs install again (which must keep the password and create no new revision), uninstalls, installs again with the same prefix (which must succeed and log in) and uninstalls again. It deletes whatever a failure left behind (about an hour and a half, and a little cost). It needs a released version whose image is published, such as a prerelease tag pushed for the purpose:

```sh
TEST_INSTALL_VERSION=0.2.0-rc.1 dotnet test --filter InstallSmokeTest
```

`TEST_INSTALL_REGION` overrides its region (westeurope).

## Releases

Pushing a tag `vX.Y.Z` (optionally with a prerelease suffix, such as `v1.2.0-rc.1`) runs `.github/workflows/release.yml`. After the tests pass, it builds every release output with `-p:Version=X.Y.Z`, so all of them report the same version:

- **CLI**: `overseer-X.Y.Z-<rid>.tar.gz` for `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`, attached to the GitHub release. Unpack the whole archive: `serve` needs the files next to the executable.
- **Container image**: `ghcr.io/pnieuwenhuis/overseer:X.Y.Z` (linux/amd64). It runs `overseer serve` on port 8080 by default and needs `OVERSEER_CONNECTIONSTRINGS__OVERSEER` and `OVERSEER_USER__PASSWORDHASH`. The workflow smoke-tests the image against a throwaway PostgreSQL before pushing it, and checks that the Portal shows the tag's version.

The Portal header and `overseer --version` show that version. A local build reports `0.0.0-dev` instead, which tells it apart from any release.

To build and smoke-test an image locally (Docker must be running):

```sh
dotnet publish src/Overseer -c Release -t:PublishContainer -p:Version=0.0.1-local
scripts/smoke-test-image.sh pnieuwenhuis/overseer:0.0.1-local
```

The repository and the `overseer` package on GHCR are public, so the CLI can be downloaded and the image pulled anonymously.

## Schema changes

The schema is defined by the EF Core model in `src/Overseer/Persistence`. After changing it, add a migration with the repository's local `dotnet-ef` tool:

```sh
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project src/Overseer --output-dir Persistence/Migrations
```
