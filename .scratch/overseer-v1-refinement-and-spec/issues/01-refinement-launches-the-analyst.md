# 01: Refinement launches the Analyst in a fresh Sandbox

**What to build:** Starting an Intent (Draft → Refining, ticket 04 of the capturing-and-starting tickets) now launches Refinement. Overseer creates a Sandbox owned by the Intent, holding its Repositories in scope. It then starts an Analyst session there with the Intent's text, its Repositories in scope and the Analyst's instructions. The Portal shows on the Intent's detail page where Refinement stands: provisioning the Sandbox (an explicit phase, since it takes minutes in Azure), then the Analyst working. Covers user stories 43 and 46. It also covers story 96 for Refinement's Sandbox.

The **Refinement** module from the spec starts here. It hooks into the Intent Lifecycle's Draft → Refining transition. It never sets Intent state itself.

The Analyst's instructions tell it that it may search the web, read the code, and build and run tests in its Sandbox. They also tell it that it must never push. Overseer's default `opencode.json` gives the Analyst agent the tools for this and denies `git push` in its permissions. The user can edit that configuration, so the hard guarantee for story 47 comes from the Sandbox itself, in ticket 09.

This ticket doesn't settle what the Analyst's turn ends with yet. Rounds come in 02 and the Spec in 03. Until then, an outcome Overseer can't act on is treated as a failed agent session.

If the Sandbox can't be created, the Intent stays Refining and keeps its slot. It turns red, shows the provider's error and offers Retry. Retry creates a Sandbox again. This is the first red condition in the Intent Lifecycle's light.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Starting an Intent creates a Sandbox owned by that Intent, holding exactly its Repositories in scope, on the Sandbox machine chosen in Settings
- [ ] Once the Sandbox is ready, an Analyst session starts in it. Its first message holds the Intent's text, the Repositories in scope and the Analyst's instructions (web search, reading code, building and running tests allowed; pushing forbidden)
- [ ] The session gets the current harness configuration and model provider tokens like any agent session
- [ ] Overseer's default `opencode.json` gives the Analyst web access and shell access, and denies `git push`
- [ ] The detail page shows the Refinement phase (provisioning the Sandbox, Analyst working) and updates it live; the light is green in both phases
- [ ] A Sandbox creation failure keeps the Intent Refining and in its slot, turns its light red with the provider's error, and offers Retry; a successful Retry turns it green again
- [ ] A failed agent session turns the Intent red with the session's error and offers Retry, which starts a new Analyst session in a fresh Sandbox
- [ ] Every Sandbox the Intent owned is destroyed once it is no longer used
- [ ] Covered by tests through the application with the fake Sandbox Provider
