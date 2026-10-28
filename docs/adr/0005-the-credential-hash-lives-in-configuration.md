# The credential hash lives in configuration, not in the database

Overseer's single user logs in with a username (the `User:Name` setting, default `overseer`) and a password whose hash is the `User:PasswordHash` setting. Like any setting, it comes from an environment variable (`OVERSEER_USER__PASSWORDHASH`) or the local config file. PostgreSQL holds no credential, unlike the v1 spec's Persistence section, which listed the hash among the database's contents. We chose this so that `install` and `reset-password` never need to reach the database: in Azure the database is private on the VNet, but setting a Container App secret is a management-plane call the CLI can always make. Locally, `reset-password` simply writes the hash to the local config file. The CLI and the server hash with the same code (`Overseer.Login.Passwords`), so a hash made by the CLI always verifies in the server.

## Consequences

- In Azure the hash is a Container App secret exposed as `OVERSEER_USER__PASSWORDHASH` (set by `install` and `reset-password`). The plaintext password is only ever printed by the CLI.
- The hash is read when the server starts, so a reset takes effect only after a restart: `reset-password` restarts the revision in Azure, and locally you restart `overseer serve`.
- A session cookie records a fingerprint of the hash it was logged in with, so a reset also ends every session opened with the old password once the server restarts.
- `serve` refuses to start without a valid hash and points at `reset-password`.
- Changing the password from the Portal, or more than one user, would need the credential to move into the database. Both are out of scope for v1.
