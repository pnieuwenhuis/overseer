# 07: Re-running `install` upgrades in place

**What to build:** Upgrading is just "get the new CLI and run install". Running `install` against an existing installation converges it to the desired state and rolls the application to the CLI's version, keeping data, secrets and the password. Covers user stories 4 and 5.

**Blocked by:** 06 (`install`: fresh installation)

**Status:** done

- [x] Install detects an existing installation by its prefix, and errors clearly if the region given differs from the existing one
- [x] Install is idempotent: existing resources are left in place, and running the same version twice changes nothing
- [x] The Container App is updated to the image tag matching the CLI's version; migrations apply on startup
- [x] The database contents and Key Vault secrets survive the upgrade
- [x] The credential secret is left untouched; the output says the existing password is kept and points at `reset-password`
- [x] Re-running after a failed, partial install completes it
- [x] Install refuses to deploy a version older than the one currently deployed, because migrations only go forward

Also decided: without `--region`, install uses the existing installation's region (westeurope for a new one). A `<prefix>-rg` or `<prefix>-sandboxes-rg` without the `overseer-prefix` tag is not taken over. When creating the Container App fails, install still prints the password, because Azure may have created the app and a re-run keeps its password. The hint names `reset-password --prefix` for a non-default prefix, which ticket 08 adds.

Known limits: a resource is found by its name, so one left in a Failed state counts as existing. The update doesn't reassign the Managed Identity, so an identity recreated by hand isn't put back on the Container App or the database. The fake `az` ignores `--query`; the queries were checked against the Azure CLI's own JMESPath and tsv output, but not against real Azure. The opt-in smoke test now runs install twice and logs in with the first password both times; like the rest of it, that part has not yet run against a real subscription.
