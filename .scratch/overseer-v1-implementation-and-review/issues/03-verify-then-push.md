# 03: Verify, then push

**What to build:** A Task counts as done only when its tests actually pass. When the agent says a Task is done, Overseer itself runs the build and test commands in the Sandbox. A failure goes back to the same agent session, with the failing command and its output, for the agent to fix. When it passes, Overseer commits what is left uncommitted, pushes the Intent branch and marks the Task Done. After the allowed number of verification attempts (setting, default 3) the Task becomes a Failed Task: the Intent turns red and no further Task runs. Covers user stories 67, 68, 69 and 71.

Decided: what is verified after a Task is every repository the Task lists, plus any other the Task changed. For each, the Repository Manifest is read as it is now, since the Task may have created or changed it, and `build` then `test` are run. When the Task changed a manifest or its Compose file, `setup` and the Compose dependencies are brought up to date first. A repository without a manifest is skipped here; ticket 05 holds it to account. An invalid manifest is a failed verification.

The branch only ever receives verified work, so it is green between Tasks.

**Blocked by:** 02 (Tasks run in order)

**Status:** ready-for-agent

- [ ] After a turn that ends as done, Overseer runs `build` and `test` for the repositories the Task lists and any other it changed
- [ ] A Task whose verification passes has its work committed and pushed to the Intent branch of every repository it changed, and only then is it Done and the next one started
- [ ] A failed verification is sent to the same agent session with the command and its output, and verification runs again after its next turn
- [ ] After the configured number of attempts the Task is Failed, the Intent turns red, nothing is pushed and no further Task runs
- [ ] Each attempt and its verification output are kept in PostgreSQL, and the detail page shows them for a Task
- [ ] A push that fails turns the Intent red with GitHub's error, and Retry tries again; the Task is not Done until the push succeeds
- [ ] Covered by tests through the application, including "tests fail twice, then pass" and a Task that never passes
