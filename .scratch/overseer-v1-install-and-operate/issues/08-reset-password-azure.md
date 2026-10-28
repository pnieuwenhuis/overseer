# 08: `reset-password` against an Azure installation

**What to build:** A user who has lost the password runs `overseer reset-password` and gets a new one for their Azure installation. It uses the same mechanism as install: generate a password, set only its hash as the Container App secret, and restart the revision so the server picks it up. Covers user story 6.

**Blocked by:** 03 (Single-user credential), 06 (`install`: fresh installation)

**Status:** ready-for-agent

- [ ] `reset-password` finds the installation with the same prefix flag as install
- [ ] It generates a new password, sets its hash as the Container App secret, restarts the active revision, and prints the username and the new password
- [ ] After the restart the new password works and the old one is rejected
- [ ] Nothing else in the installation changes
- [ ] A clear error appears when no installation exists for the prefix
