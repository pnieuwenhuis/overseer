# 04: Fake Sandbox Provider

**What to build:** The Sandbox Provider port, plus a fake implementation, so a developer can work on the Portal and orchestration offline and tests can script Sandbox behaviour without any VM. This is the primary test seam named in the spec's Testing Decisions. Covers user story 14.

**Blocked by:** 01 (Walking skeleton)

**Status:** done

- [x] The Sandbox Provider interface covers: create a Sandbox for an owner (an Intent or a Repository Setup) with its Repositories in scope; run a command and get its exit code and output; start an agent session for a role (streamed updates plus a structured outcome such as "done" or a question for the user); destroy; list existing Sandboxes with their owner tags
- [x] The provider is selected through configuration, and the fake is used when no Azure provider is configured
- [x] The fake can be scripted per command, including sequences such as "tests fail twice, then pass"
- [x] The fake can be scripted per agent session: streamed updates, "done", or "asks the user a question"
- [x] The fake can simulate a Sandbox creation failure with an error message, and a slow provisioning phase
- [x] Sandboxes are recorded in PostgreSQL with owner, provider resource identifier and created-at, and the record reflects destroy
- [x] A provider contract test suite, runnable against any implementation, passes for the fake in the default test run
