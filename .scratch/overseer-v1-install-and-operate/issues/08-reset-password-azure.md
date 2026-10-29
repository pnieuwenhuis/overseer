# 08: `reset-password` against an Azure installation

**What to build:** A user who has lost the password runs `overseer reset-password` and gets a new one for their Azure installation. It uses the same mechanism as install: generate a password, set only its hash as the Container App secret, and restart the revision so the server picks it up. Covers user story 6.

**Blocked by:** 03 (Single-user credential), 06 (`install`: fresh installation)

**Status:** done

- [x] `reset-password` finds the installation with the same prefix flag as install
- [x] It generates a new password, sets its hash as the Container App secret, restarts the active revision, and prints the username and the new password
- [x] After the restart the new password works and the old one is rejected
- [x] Nothing else in the installation changes
- [x] A clear error appears when no installation exists for the prefix

Also decided: `overseer reset-password` resets an installation in Azure (prefix `overseer` unless `--prefix`), as install's hint already said; `reset-password --local` is the local reset of ticket 03, and serve's "no password" errors point at it. `--local` with `--prefix` is refused. The username printed is the Container App's own `OVERSEER_USER__NAME`, not the local config's. The restart restarts the newest active revision and waits until a replica started by it is ready (one whose name the revision didn't have before) and the replicas it replaces, which still have the old hash, are gone. If the restart fails, the password is still printed, because the hash is already set, with the `az` command to restart it by hand: another reset would replace that password. If setting the secret fails, no password is printed and a re-run starts over.

Known limits: as for install, the fake `az` ignores `--query`, so the queries in `set-password-hash.sh` and `restart-container-app.sh` haven't been checked against real Azure. That setting a secret creates no revision, and that a revision restart starts new replicas under new names before the old ones go, come from the Container Apps documentation and behaviour, not from a run. The opt-in smoke test now resets the password between the two installs and checks that only the new one logs in; like the rest of it, it has not run against a real subscription yet.
