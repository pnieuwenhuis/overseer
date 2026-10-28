# Overseer

Overseer lets one person direct a team of AI agents, installed into their own Azure subscription. Its Portal lets you capture Intents, refine them into Specs with an Analyst agent, have a Squad of agents implement them in Sandboxes, and review and merge the result. See [the v1 spec](docs/specs/overseer-v1.md), the [glossary](GLOSSARY.md) and the [ADRs](docs/adr/).

Overseer is a single `overseer` executable (ADR 0004). `overseer serve` runs the Portal and the orchestration; `install`, `uninstall` and `reset-password` manage the Azure deployment. Run `overseer` without a command to list them, and `overseer --version` for its version.

## Running Overseer locally

You need the .NET 10 SDK and Docker.

1. Start PostgreSQL:

   ```sh
   docker compose up -d
   ```

   This starts PostgreSQL on host port **5432**, with a named volume so data survives restarts. `docker compose down -v` throws the data away.

2. Generate a password:

   ```sh
   dotnet run --project src/Overseer -- reset-password
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

| Setting        | Config file key              | Environment variable                   | Default                                                                                     |
| -------------- | ---------------------------- | -------------------------------------- | ------------------------------------------------------------------------------------------- |
| Database       | `ConnectionStrings:Overseer` | `OVERSEER_CONNECTIONSTRINGS__OVERSEER` | none; `serve` exits with a message when it is missing (the compose database in Development) |
| Listen address | `Urls`                       | `OVERSEER_URLS`                        | `http://localhost:5080`                                                                     |
| Username       | `User:Name`                  | `OVERSEER_USER__NAME`                  | `overseer`                                                                                  |
| Password hash  | `User:PasswordHash`          | `OVERSEER_USER__PASSWORDHASH`          | none; `serve` exits with a message pointing at `reset-password`                            |

For example, to listen on another port for one run:

```sh
OVERSEER_URLS=http://localhost:5090 dotnet run --project src/Overseer
```

## Tests

```sh
dotnet test
```

Docker must be running: the server tests start Overseer in-process against a real PostgreSQL through Testcontainers.

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
