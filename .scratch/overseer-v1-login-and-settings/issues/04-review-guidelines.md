# 04: Review Guidelines

**What to build:** The user maintains global Review Guidelines on the Settings page, as Markdown text with a preview. They are stored as a setting and kept across restarts. Later, the Reviewer receives them with every review. Covers user story 23.

**Blocked by:** 02 (Settings page with operating limits)

**Status:** done

- [x] The Settings page has a Markdown editor for the Review Guidelines, with a rendered preview
- [x] Saved Review Guidelines survive a restart of `serve`; the initial value is empty
- [x] Server-side code can read the current Review Guidelines through the settings service from 02
- [x] Covered by tests through the application
