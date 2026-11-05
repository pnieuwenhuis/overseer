# 03: Edit and delete a Draft

**What to build:** Each Intent has a detail page, reached from the Backlog. While the Intent is a Draft, the user can edit its text and its Repositories in scope, and can delete it outright after confirming. A deleted Draft is gone, not Cancelled. Covers user stories 34 and 36.

**Blocked by:** 02 (Repositories in scope)

**Status:** done

- [x] Clicking an Intent in the Backlog opens its detail page, showing its rendered text, state and Repositories in scope
- [x] On a Draft, the user can edit the text (with preview) and the Repositories in scope and save; empty text is refused
- [x] On a Draft, the user can delete the Intent after confirming; it disappears from the Backlog and from PostgreSQL, including its Repositories in scope
- [x] Deleting a Draft frees the Configured Repositories it had in scope for removal
- [x] Covered by tests through the application
