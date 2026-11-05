# 02: Repositories in scope

**What to build:** When creating an Intent, the user selects its Repositories in scope from the Configured Repositories; all of them are selected by default. The selection is stored with the Intent and shown on it. Settings refuses to remove a Configured Repository that an open Intent has in scope, and names that Intent. Covers user stories 32 and 18 (the guard was deferred from ticket 06 of the login-and-settings tickets).

Decided: a Draft may be saved with no Repositories in scope, so the Backlog can be filled before any repository is configured. Starting an Intent requires at least one (ticket 04).

**Blocked by:** 01 (Capture a Draft Intent in the Backlog)

**Status:** done

- [x] Creating an Intent offers every Configured Repository, all selected by default; the user can deselect any
- [x] The Repositories in scope are stored with the Intent and survive a restart
- [x] A Draft can be saved with no Repositories in scope
- [x] Removing a Configured Repository that an open Intent (any state except Done and Cancelled) has in scope is refused with a message naming the Intent; nothing is removed
- [x] A Configured Repository that no open Intent has in scope can still be removed
- [x] Covered by tests through the application with the fake GitHub Gateway
