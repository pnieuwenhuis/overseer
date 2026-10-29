# 09: `uninstall`

**What to build:** A user who wants to stop paying for Overseer runs `overseer uninstall`. After a confirmation, it removes everything Overseer created in the subscription and nothing else. Covers user story 7.

**Blocked by:** 06 (`install`: fresh installation)

**Status:** done

- [x] Uninstall lists what it will remove (both resource groups by name) and requires the user to type the prefix to confirm; a flag skips the prompt for scripted use such as the smoke test
- [x] It removes the Sandbox resource group (including any remaining Sandboxes), the Overseer resource group and the Managed Identity's role assignments
- [x] It purges the soft-deleted Key Vault, so a later install with the same prefix succeeds
- [x] It removes only resources Overseer created
- [x] Running uninstall when nothing is installed reports that there is nothing to remove and exits successfully
- [x] An opt-in smoke test runs install, then uninstall, then install with the same prefix, and the last install succeeds

Also decided: `overseer uninstall [--prefix overseer] [--yes]`. It removes, in this order: the Managed Identity's role assignments (all of them, found by principal ID, so none are orphaned and the running Overseer loses its rights on the Sandbox resource group first), `<prefix>-sandboxes-rg`, the Log Analytics workspace for good (`--force`: deleting its group only soft-deletes it, and a reinstall within 14 days would recover the old installation's logs), `<prefix>-rg`, then purges the soft-deleted Key Vault. Each resource group is removed only when tagged `overseer-prefix=<prefix>`, checked again just before deleting it; an untagged one is named and left alone, and the identity and workspace in an untagged `<prefix>-rg` aren't touched. A soft-deleted vault with the installation's Key Vault name (prefix plus the subscription's hash) counts as Overseer's, so a re-run after an uninstall that stopped before the purge purges it. Typing anything but the prefix, or no input at all, removes nothing and exits 1. `OverseerCli.InvokeAsync` gained an input reader for the confirmation; the overloads without one read no input.

Known limits: as before, the fake `az` ignores `--query`, so the queries in the new scripts haven't been checked against real Azure. Whether Azure lets the workspace go while the Container Apps environment still sends to it, and whether the PostgreSQL server name and its private DNS zone are free again right after their group is deleted, only a real run shows. The opt-in smoke test now uninstalls (checking the role assignments, both groups and the deleted vault are gone), installs again with the same prefix and logs in, and uninstalls again; like the rest of it, it has not run against a real subscription yet, and it puts nothing in the Sandbox resource group.
