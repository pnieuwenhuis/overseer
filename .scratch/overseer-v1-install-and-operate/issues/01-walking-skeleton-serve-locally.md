# 01: Walking skeleton: `overseer serve` runs locally

**What to build:** A developer runs `overseer serve` on their own machine against a local PostgreSQL and opens the Portal in a browser. This is the foundation every later ticket builds on: one `overseer` executable with a command structure (ADR 0004), a Blazor Web App with interactive Server rendering, PostgreSQL with schema migrations applied at startup, and layered configuration. Covers user story 12.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] `overseer serve` starts the Portal and orchestration in a single process; the Portal shell page is reachable locally and shows the running version
- [x] `overseer` lists its commands (`serve`, `install`, `uninstall`, `reset-password`); an unknown command prints usage and exits non-zero
- [x] Overseer connects to a local PostgreSQL given through configuration, and there is a documented one-step way for a developer to start one
- [x] Schema migrations are applied at startup: an empty database gets the schema, and starting again on an up-to-date database changes nothing
- [x] Configuration is layered: environment variables override the local config file, which overrides defaults (covered by tests)
- [x] The orchestration test harness exists: the application runs in-process against a real PostgreSQL (Testcontainers), with at least one test proving startup and migrations
- [x] How to run Overseer locally is documented for developers and agents
