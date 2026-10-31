# 07: Agent harness configuration

**What to build:** On the Settings page the user edits the agent harness configuration, an `opencode.json`. Overseer ships a default. The page offers the raw JSON plus a model picker for each role (Analyst, Designer, Developer, Reviewer). The picker edits the same JSON, so the two always agree. A configuration is validated before it is saved. Every agent session started after a save gets the current configuration. Covers user story 24.

The real ACP Agent Session (OpenCode over SSH) doesn't exist yet. This ticket hands the configuration to whatever starts an agent session, and the fake Sandbox Provider records what each session received.

**Blocked by:** 02 (Settings page with operating limits)

**Status:** done

- [x] Before the first save, the Settings page shows Overseer's default `opencode.json`, which sets a model for every role
- [x] The raw JSON can be edited and saved; invalid JSON is rejected with the parser's message and nothing is saved
- [x] A configuration that lacks a model for any of the four roles is rejected, naming the role
- [x] The per-role model picker shows each role's current model and changes it in the JSON; edits in the raw JSON show up in the picker
- [x] The configuration survives a restart; the user can restore the default
- [x] Each agent session started receives the configuration current at its start, with its role's model (verified through the fake Sandbox Provider)
- [x] Covered by tests through the application
