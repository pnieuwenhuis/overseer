# 09: `uninstall`

**What to build:** A user who wants to stop paying for Overseer runs `overseer uninstall`. After a confirmation, it removes everything Overseer created in the subscription and nothing else. Covers user story 7.

**Blocked by:** 06 (`install`: fresh installation)

**Status:** ready-for-agent

- [ ] Uninstall lists what it will remove (both resource groups by name) and requires the user to type the prefix to confirm; a flag skips the prompt for scripted use such as the smoke test
- [ ] It removes the Sandbox resource group (including any remaining Sandboxes), the Overseer resource group and the Managed Identity's role assignments
- [ ] It purges the soft-deleted Key Vault, so a later install with the same prefix succeeds
- [ ] It removes only resources Overseer created
- [ ] Running uninstall when nothing is installed reports that there is nothing to remove and exits successfully
- [ ] An opt-in smoke test runs install, then uninstall, then install with the same prefix, and the last install succeeds
