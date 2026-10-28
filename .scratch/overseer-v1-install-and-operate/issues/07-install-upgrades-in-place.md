# 07: Re-running `install` upgrades in place

**What to build:** Upgrading is just "get the new CLI and run install". Running `install` against an existing installation converges it to the desired state and rolls the application to the CLI's version, keeping data, secrets and the password. Covers user stories 4 and 5.

**Blocked by:** 06 (`install`: fresh installation)

**Status:** ready-for-agent

- [ ] Install detects an existing installation by its prefix, and errors clearly if the region given differs from the existing one
- [ ] Install is idempotent: existing resources are left in place, and running the same version twice changes nothing
- [ ] The Container App is updated to the image tag matching the CLI's version; migrations apply on startup
- [ ] The database contents and Key Vault secrets survive the upgrade
- [ ] The credential secret is left untouched; the output says the existing password is kept and points at `reset-password`
- [ ] Re-running after a failed, partial install completes it
- [ ] Install refuses to deploy a version older than the one currently deployed, because migrations only go forward
