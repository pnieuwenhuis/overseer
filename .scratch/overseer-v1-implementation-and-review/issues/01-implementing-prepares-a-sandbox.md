# 01: Implementing prepares a Sandbox

**What to build:** Moving an Intent to Implementing ("Approve & start", or Start on an Approved Intent) now does something. Overseer creates the Intent's implementation Sandbox, with push access, and gets it ready for the Squad: each Repository in scope is cloned and put on the Intent branch, its Repository Manifest is read, its `setup` command is run and its Compose dependencies are started. The Portal shows which phase this is in, as provisioning takes minutes in Azure. When any step fails, the Intent turns red with the error, keeps its slot and state, and offers Retry. Covers user story 63. It also covers story 96 for implementation.

This is where the Repository Manifest is first read rather than only checked for: `.overseer.yml` with the commands `setup`, `build` and `test`, and optionally the path of a Compose file as `compose`. A repository without one is not an error here, since a Task may still create it; nothing is set up or started for it. A manifest that can't be read or is invalid fails the preparation.

The Intent branch is one per Intent, with the same name in every Repository in scope and stable for the Intent's life. It is created from the default branch, or checked out when it already exists.

Refinement is so far the only module that can fail an Intent or ask for the human's attention. The Intent Lifecycle has to take this from several modules first.

A stopgap until ticket 10: an implementation that a restart of `serve` interrupts is marked failed, with Retry, also for Intents that were already Implementing before this ticket.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] An Intent that moves to Implementing gets a new Sandbox it owns, with push access and the Repositories in scope
- [x] In each Repository in scope the Sandbox is on the Intent branch, created from the default branch or checked out when it exists
- [x] For each Repository in scope with a Repository Manifest, `setup` is run and the services of its Compose file are started and awaited
- [x] The detail page shows the current phase live, including the provisioning of the Sandbox, and that the Intent is ready once preparation succeeds
- [x] A Sandbox that can't be created, a failing `setup` or Compose start, and an invalid manifest each turn the Intent red with the error, naming the repository where one is at fault
- [x] Retry prepares again in a fresh Sandbox; the failed one is destroyed
- [x] Repository Manifest parsing and validation are tested directly with sample files, including a missing manifest and invalid ones
- [x] Failures and Needs Attention from more than one module reach an Intent's light
- [x] A restart of `serve` leaves an interrupted implementation failed, with Retry
- [x] Covered by tests through the application with the fake Sandbox Provider
