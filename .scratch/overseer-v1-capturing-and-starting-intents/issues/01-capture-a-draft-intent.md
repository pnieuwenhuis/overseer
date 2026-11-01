# 01: Capture a Draft Intent in the Backlog

**What to build:** The Portal's Home page becomes the Backlog. The user creates an Intent by writing free text in Markdown, with a rendered preview, and saves it as a Draft. The Intent is stored in PostgreSQL and appears in the Backlog with its state. Covers user stories 31 and 33.

Decided: an Intent has no separate title field. The Backlog shows the first line of its text (Markdown heading markers stripped). The spec rules out an Intent template.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] The Home page shows the Backlog and offers "New Intent"
- [x] The user writes the Intent's text in a Markdown editor with a rendered preview; empty text is refused
- [x] Saving creates the Intent as a Draft, stored in PostgreSQL; it survives a restart of `serve`
- [x] The Backlog lists each Intent by the first line of its text, with its state (Draft)
- [x] The schema change is an EF Core migration applied at startup
- [x] Covered by tests through the application (`RunningOverseer`), against a real PostgreSQL
