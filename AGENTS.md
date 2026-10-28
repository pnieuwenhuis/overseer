# Overseer

.NET 10 solution (`Overseer.slnx`): one `overseer` executable in `src/Overseer` (CLI and Blazor Server Portal, ADR 0004) and tests in `tests/Overseer.Tests`. Domain terms follow `GLOSSARY.md`; ADRs in `docs/adr/` are binding.

Running locally, configuration layering and adding migrations are documented in `README.md`. Read it before running `serve` or changing the schema.

## Gotchas

- `dotnet test` needs Docker running: server tests start a PostgreSQL through Testcontainers.
- The compose PostgreSQL listens on host port 55432, not 5432.
- `dotnet run --project src/Overseer` runs `serve` only through the `serve` launch profile, which sets the Development environment and with it the compose connection string.
- Keep `Microsoft.EntityFrameworkCore.Design` and the `dotnet-ef` tool on the EF Core version that `Npgsql.EntityFrameworkCore.PostgreSQL` depends on. A higher version breaks the test build (CS1705).
- The first `serve` against an empty database logs `fail: ... Failed executing DbCommand ... FROM "__EFMigrationsHistory"`. That is EF Core probing for its history table before creating it; the migration still succeeds.
- Server tests go through `RunningOverseer` (the real server in-process on a free port, with its own empty database). Pass settings as the environment map it hands to `OverseerServer`, never as process environment variables, so tests stay parallel-safe.
