# 03: Single-user credential, minimal login, and local `reset-password`

**What to build:** Overseer has one user. Their password hash comes from configuration (an environment variable, or the local config file when run locally) and is never stored in the database. A minimal login page with ASP.NET Core cookie authentication protects the Portal. Locally, `overseer reset-password` generates a new password, prints it once, and writes its hash to the local config file so the next `serve` accepts it. Covers user story 6 (locally) and the bare minimum of story 15, needed so that "log in immediately" (story 2) can be verified.

Decided: the credential hash lives only in configuration, not in PostgreSQL (for now). This deviates from the spec's Persistence section and must be recorded in an ADR: in Azure the hash is a Container App secret exposed as an env var (set by 06 and 08), it's applied when the server starts, and a reset needs a restart.

**Blocked by:** 01 (Walking skeleton)

**Status:** done

- [x] Every Portal page except the login page requires authentication
- [x] Logging in with the configured username and the correct password succeeds; a wrong password fails (covered by tests through the application)
- [x] The plaintext password is never stored or logged anywhere; the database holds no credential
- [x] `serve` without a configured hash fails fast with a clear message pointing at `reset-password`
- [x] Local `overseer reset-password` generates a strong random password, prints the username and password once, and writes the hash to the local config file; after a restart the new password works and the old one is rejected
- [x] The CLI and the server use the same hashing code, so a hash made by the CLI always verifies in the server
- [x] An ADR records the "hash only in configuration" decision and its consequences
- [x] Account lockout is out of scope (story 16)
