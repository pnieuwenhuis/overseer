# 03: Sandbox VM size and spot instances

**What to build:** On the Settings page the user chooses the Sandbox VM size from a fixed list, `Standard_D2_v5` or `Standard_D4s_v5`, and whether Sandboxes run as Azure spot instances. The next Sandbox created uses the chosen size and priority. Existing Sandboxes are left alone. This replaces the `SandboxProvider:Azure:VmSize` configuration key: the VM size becomes a setting the user changes in the Portal, not a deployment detail. Covers user story 29.

Defaults: `Standard_D4s_v5` (the Azure provider's current default), without spot.

**Blocked by:** 02 (Settings page with operating limits)

**Status:** done

- [x] The Settings page offers exactly `Standard_D2_v5` and `Standard_D4s_v5`, plus a "use spot instances" option, with the defaults above
- [x] The Azure Sandbox Provider creates each new Sandbox with the size and priority set when it is created; a spot Sandbox is created with eviction policy Delete
- [x] The `SandboxProvider:Azure:VmSize` configuration key is removed from code, `install` and the README; the startup log no longer reports a configured size
- [x] When Azure has no spot capacity or quota, Sandbox creation fails with Azure's own error message, as any creation failure does
- [x] The fake Sandbox Provider records the size and priority each Sandbox was created with, so tests can check that a change applies to the next Sandbox only
- [x] The Azure provider's opt-in contract test covers creating a spot Sandbox
