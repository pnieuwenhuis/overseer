# 02: Settings page with operating limits

**What to build:** Overseer has Settings, stored in PostgreSQL, and a Settings page reachable from the Portal navigation. This ticket brings the first settings, the operating limits: maximum Active Intents (default 2), Refinement idle timeout (default 30 minutes), verification attempts per Task (default 3) and maximum Sandbox lifetime (default 12 hours). The modules that use them (Intent Lifecycle, Refinement, Task Runner, Sandbox Reconciler) don't exist yet. They will read the current values from one place that this ticket provides. Covers user stories 26, 27, 28 and 30.

Settings live in PostgreSQL. Secrets do not: they stay in the secret store.

Decided: the upper bounds are 10 Active Intents, 480 minutes of Refinement idle timeout, 10 verification attempts per Task and 48 hours of Sandbox lifetime. The page saves the four limits together, so one invalid value saves none of them.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] The Portal navigation links to a Settings page; like every other page it requires login
- [x] The Settings page shows the four operating limits with their current values; an unset limit shows its default
- [x] Saving a valid value stores it in PostgreSQL; it survives a restart of `serve`
- [x] Invalid values (zero, negative, non-numeric, an absurd upper bound) are rejected with a message, and nothing is saved
- [x] Server-side code reads the current value of each setting through one service, which returns the default when none was saved; a saved change is visible to it without a restart
- [x] The schema change is an EF Core migration applied at startup
- [x] Covered by tests through the application, against a real PostgreSQL
