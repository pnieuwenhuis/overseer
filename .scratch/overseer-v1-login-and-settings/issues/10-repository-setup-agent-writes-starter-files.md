# 10: Repository Setup: the agent writes the starter files

**What to build:** After the repository is created (09), Overseer creates a temporary Sandbox owned by the Repository Setup, containing the new repository. It runs a one-off agent session there with the stated purpose and tech stack. The agent writes a starter README, agent guidance (`AGENTS.md`) and a Repository Manifest (`.overseer.yml`), and pushes them directly to main. The Sandbox is then destroyed. The Portal shows the Repository Setup's progress, including the slow Sandbox provisioning phase as an explicit step. A failure shows its error and offers Retry. Covers user story 21.

This ticket is verified against the fake Sandbox Provider and its scripted agent sessions. Running it for real also needs the ACP Agent Session module (OpenCode over SSH), which isn't built yet.

**Blocked by:** 09 (Repository Setup: create and register a repository), 07 (Agent harness configuration), 08 (Model provider tokens)

**Status:** done

- [x] After the repository is created, Overseer creates a Sandbox owned by the Repository Setup with the new repository in it
- [x] An agent session receives the purpose, tech stack and instructions to write the README, `AGENTS.md` and `.overseer.yml` and push them to main; it gets the current harness configuration and provider tokens like any agent session
- [x] The Sandbox is destroyed when the setup finishes, whether it succeeds or fails
- [x] The Portal shows the Repository Setup's phase (creating the repository, provisioning the Sandbox, agent working, done or failed); it updates live
- [x] A Sandbox creation failure or a failed agent session marks the Repository Setup failed with the error; Retry starts the agent part again without re-creating the repository
- [x] After a successful setup, Overseer confirms that `.overseer.yml` is on main (through the Sandbox or the Gateway); a missing manifest fails the setup
- [x] A Repository Setup interrupted by a restart of `serve` is shown as failed and can be retried, with no Sandbox left behind
- [x] Covered by tests through the application with the fake Sandbox Provider and the fake GitHub Gateway
