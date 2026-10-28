---
title: Overseer v1
labels: [ready-for-agent]
---

# Overseer v1

Terminology follows `GLOSSARY.md`. Architectural decisions are recorded in `docs/adr/` (0001–0004) and are binding for this spec.

## Problem Statement

I want to build and evolve software by directing AI agents rather than writing every line myself. Today that means juggling agent sessions on my own machine. I have to:

- explain the same context again and again
- watch terminals to see whether an agent is stuck
- set up databases and test dependencies by hand
- review whatever the agent produced without a clear contract of what it was supposed to do

There is no single place where I capture what I want, agree with an agent on *how* it will be built, let agents do the work in isolated environments, see at a glance which work needs me, and review and merge the result. Running all of this on my laptop also ties the work to my laptop being on.

## Solution

Overseer is a factory I install into my own Azure subscription with one command. It gives me a Portal where I:

1. Capture **Intents**: free-text descriptions of a change I want, in a Backlog.
2. Start **Refinement** on an Intent. An **Analyst** agent studies the code in a **Sandbox** and asks me questions in **Rounds** until it can write a **Spec** with an ordered list of **Tasks**.
3. Review, edit and approve the Spec.
4. Start implementation. A **Squad** of agents (Designer, Developer) carries out the Tasks one at a time in a Sandbox. Overseer verifies each Task by running the repository's build and tests in that Sandbox.
5. Get pull requests reviewed by a **Reviewer** agent that also runs the application and attaches screenshots. I triage the Review Comments, let an agent process the accepted ones, and approve.
6. Overseer merges the pull requests, and the Intent is **Done**.

A traffic light per Intent shows whether agents are working, need me, or have failed. I can pause or cancel any Intent at any time. At most a configurable number of Intents (default 2) are active at once, to keep cost under control.

## User Stories

### Installing and operating Overseer

1. As the user, I want to install Overseer into my current Azure subscription with a single CLI command, so that I don't have to provision resources by hand.
2. As the user, I want the install command to print the Portal URL, a username and a freshly generated password, so that I can log in immediately.
3. As the user, I want to choose the Azure region and resource name prefix with CLI flags, defaulting to westeurope, so that I can follow my own conventions.
4. As the user, I want running install again to upgrade an existing installation in place, so that upgrading is just "get the new CLI and run install".
5. As the user, I want the deployed application version to always match the CLI version I ran, so that the CLI and the server never drift apart.
6. As the user, I want a reset-password command, so that I can regain access if I lose the password.
7. As the user, I want an uninstall command that asks for confirmation and removes everything Overseer created, so that I can cleanly stop paying for it.
8. As the user, I want Overseer's identity to have rights only on the Sandbox resource group, so that a bug or a misbehaving agent can't touch other resources in my subscription.
9. As the user, I want secrets (the GitHub token, model provider tokens) stored in Key Vault and never displayed again after entry, so that they don't leak through the Portal or the database.
10. As the user, I want the database to use identity-based authentication instead of a password, so that there are no database credentials to manage.
11. As the user, I want Sandboxes to have no public IP when Overseer runs in Azure, so that nothing in them is reachable from the internet.
12. As a developer of Overseer, I want to run Overseer locally with the same executable and a local database, so that I can work on it without deploying.
13. As a developer of Overseer, I want a local Overseer to still be able to create and use real Sandboxes in Azure, reachable only from my own IP, so that I can test end-to-end from my machine.
14. As a developer of Overseer, I want a fake Sandbox provider, so that I can work on the Portal and orchestration offline.

### Logging in

15. As the user, I want to log in to the Portal with my username and password, so that only I can control the factory.
16. As the user, I want repeated failed logins to lock the account temporarily, so that the public Portal can't be brute-forced.

### Settings

17. As the user, I want to register GitHub repositories as Configured Repositories, so that Overseer knows which repositories make up my software.
18. As the user, I want to be prevented from removing a Configured Repository that an open Intent has in scope, so that I don't break work in progress.
19. As the user, I want to store and replace my GitHub fine-grained token, so that Overseer and its agents can work with my repositories.
20. As the user, I want to create a new GitHub repository from the Portal by stating its purpose and tech stack, so that I can start a new component without leaving Overseer.
21. As the user, I want a new repository to be given a starter README, agent guidance and Repository Manifest by an agent, so that it's immediately ready for Intents.
22. As the user, I want Repository Setup not to take one of my Active Intent slots, so that creating a repository doesn't block other work.
23. As the user, I want to maintain global Review Guidelines, so that every review checks what matters to me.
24. As the user, I want to edit the agent harness configuration, including which model each role uses, so that I can use stronger models where they matter and cheaper ones elsewhere.
25. As the user, I want to store model provider tokens and switch providers (for example Azure Foundry or an AI gateway) without changing Overseer, so that I'm not locked into one provider.
26. As the user, I want to set the maximum number of Active Intents (default 2), so that I control cost and my own review capacity.
27. As the user, I want to set the Refinement idle timeout (default 30 minutes), so that idle Refinements don't keep machines running.
28. As the user, I want to set how many verification attempts a Task gets before it fails (default 3), so that stuck agents don't loop forever.
29. As the user, I want to choose the Sandbox VM size, so that I can balance speed against cost.
30. As the user, I want to set a maximum Sandbox lifetime (default 12 hours), so that a forgotten or runaway Sandbox can't cost me indefinitely.

### Capturing Intents

31. As the user, I want to create an Intent by writing free text in Markdown, so that I can describe what I want in my own words.
32. As the user, I want to select the Repositories in scope for an Intent, defaulting to all Configured Repositories, so that agents only touch what's relevant.
33. As the user, I want to save an Intent as a Draft and start it later, so that I can build up a Backlog.
34. As the user, I want to edit an Intent's text while it's a Draft, so that I can refine my thoughts before agents get involved.
35. As the user, I want an Intent's text to be locked once Refinement starts, so that the original request stays intact as the reference for what I asked for.
36. As the user, I want to delete a Draft outright, so that ideas I've dropped don't clutter the Backlog.
37. As the user, I want to see a list of Intents filtered on Open and Closed, so that I can focus on what's still in progress.
38. As the user, I want each Intent in the list to show its state and a light (green for working, orange for Needs Attention, red for failed), so that I can see where I'm needed at a glance.
39. As the user, I want each Intent to show its Working Time, so that I know how much active effort it has taken.

### Starting work and concurrency

40. As the user, I want to start work on an Intent explicitly, so that nothing starts without my decision.
41. As the user, I want Start to be disabled, with an explanation, when all Active Intent slots are taken, so that I understand why I can't start and what to do about it.
42. As the user, I want Intents that are waiting on me for approval, or are Paused, not to occupy a slot, so that slots are only used by Intents where agents are actually working.

### Refinement

43. As the user, I want starting an Intent to launch Refinement in a fresh Sandbox containing the Repositories in scope, so that the Analyst can ground its questions in the real code.
44. As the user, I want the Analyst to ask me questions in numbered Rounds, each with its suggested answer, so that I can answer quickly and only elaborate where I disagree.
45. As the user, I want to answer a question with "as suggested" or leave it blank, so that the Analyst's suggestion is recorded as an Analyst's Choice.
46. As the user, I want the Analyst to be able to search the web, read code, and build and run tests in its Sandbox, so that its questions and Spec are well informed.
47. As the user, I want the Analyst to be unable to push anything during Refinement, so that Refinement never changes my repositories.
48. As the user, I want the Analyst to decide when it has enough information to write the Spec, so that Specs aren't written on insufficient information.
49. As the user, I want the Analyst to ask me whether external dependencies (such as a database) are needed for testing, and to add a Task to create a Docker Compose setup when they are needed and missing, so that the Squad can run integration tests.
50. As the user, I want the Analyst to add a Task to create a Repository Manifest when a Repository in scope lacks one, so that Overseer can verify work in that repository.
51. As the user, I want the Refinement transcript saved, so that I can leave and come back without losing the conversation.
52. As the user, I want an idle Refinement to be paused automatically after the configured timeout, so that its Sandbox is released and its slot freed.
53. As the user, I want a resumed Refinement to continue with the Analyst aware of the earlier transcript, so that I don't have to repeat myself.

### Spec

54. As the user, I want the Spec to contain a summary, approach, decisions (with Analyst's Choices marked), acceptance criteria and dependencies, so that I can judge whether the plan is right.
55. As the user, I want the Spec to include API endpoint, data model and UI changes when they apply, so that I can see the impact at a glance.
56. As the user, I want the Spec's Tasks to be an ordered list, each with a title, description, role and the repositories it touches, so that I know exactly what will be done in which order.
57. As the user, I want to edit the Spec and its Tasks directly, creating a new Spec version, so that I can make small corrections myself.
58. As the user, I want to reorder Tasks before approval, so that work happens in the order I think is right.
59. As the user, I want to send a Spec back to Refinement with comments, so that the Analyst revises it.
60. As the user, I want to approve a Spec, which locks that version as the Approved Spec, so that there is a fixed contract for implementation and review.
61. As the user, I want "Approve & start" when a slot is free, and "Approve" otherwise, so that I can approve without being able to start yet.
62. As the user, I want to start an Approved Intent later, so that I control when implementation begins.

### Implementation

63. As the user, I want implementation to run in one Sandbox per Intent, with the Repositories in scope cloned, their setup commands run and their dependencies started, so that agents work in a realistic environment.
64. As the user, I want Tasks to be carried out one at a time in order, each by a fresh agent session for the Task's role, so that agents never get in each other's way.
65. As the user, I want Designer Tasks to produce working UI code with placeholder data, plus screenshots, so that I can see the design before Developers build on it.
66. As the user, I want Developer Tasks to always include unit and integration tests for their own work, so that quality is built in.
67. As the user, I want Overseer itself to run the build and test commands in the Sandbox when an agent says a Task is done, so that a Task counts as done only when its tests actually pass.
68. As the user, I want failed verification to be fed back to the agent, so that it can fix its work.
69. As the user, I want a Task that still fails after the allowed attempts to become a Failed Task and turn the Intent red, so that I know I need to step in.
70. As the user, I want to retry a Failed Task, edit it and retry, or pause the Intent for a Revision, so that I have clear ways to recover.
71. As the user, I want each verified Task's work pushed to the Intent branch, so that the branch is always green between Tasks.
72. As the user, I want to see the Task list with each Task's state and role, plus a one-line "currently doing" summary for the running Task, so that I can follow progress without reading logs.
73. As the user, I want to open the live agent session stream of a running Task, so that I can see exactly what the agent is doing.
74. As the user, I want to send a message to the running agent, so that I can steer it when needed.
75. As the user, I want an agent to be able to ask me a question, which turns the Intent orange, so that it doesn't guess when it's truly stuck.
76. As the user, I want Overseer to run the full build and tests on the Intent branch once all Tasks are done, and to add a fix-up Task if they fail, so that pull requests are only opened for green code.
77. As the user, I want one pull request per repository that actually changed, and none for unchanged repositories, so that the pull requests reflect the real change.

### Review

78. As the user, I want the Reviewer to review the pull requests in the Intent's Sandbox against the Approved Spec, the Review Guidelines and each repository's agent guidance, so that the review is about what was agreed.
79. As the user, I want the Reviewer to start the application in the Sandbox, exercise the API endpoints and UI changes listed in the Spec, and take screenshots with a headless browser, so that the review covers behaviour, not just code.
80. As the user, I want the Reviewer's comments and screenshots posted on the GitHub pull request, so that the pull request has the full review history.
81. As the user, I want to see all Review Comments in the Portal and accept or dismiss each one with an optional note, so that I decide what gets changed.
82. As the user, I want comments I write directly on the GitHub pull request to show up in the Portal as accepted, so that I can review wherever I prefer.
83. As the user, I want dismissed comments resolved on GitHub with my note as the reply, so that the pull request reflects my decisions.
84. As the user, I want "Process comments" to create one Developer Task containing all accepted comments and my notes, so that an agent addresses them in one go.
85. As the user, I want the Reviewer to re-review only the changes since its last review after comments are processed, so that review cycles stay focused.
86. As the user, I want to approve the Intent at any time during review, so that I always have the final say.
87. As the user, I want approval to squash-merge every pull request of the Intent and delete the Intent branch, so that main gets a clean history.
88. As the user, I want an Intent to be Done only when all its pull requests are merged, and to turn red when a merge fails, so that a partially merged Intent is never reported as finished.

### Pause, resume, Revision and cancel

89. As the user, I want to pause an Intent that is Refining, Implementing or In Review, so that I can free its slot and stop costs.
90. As the user, I want pausing to push any unfinished work as a clearly marked work-in-progress commit and destroy the Sandbox, so that a paused Intent costs nothing.
91. As the user, I want resuming to bring the Intent back to the state it was paused from, in a fresh Sandbox, with the interrupted Task continuing from the work-in-progress commit, so that no work is lost.
92. As the user, I want to start a Revision on a Paused Intent, sending it back to Refinement while keeping the branch, so that I can change course when implementation shows the Spec was wrong.
93. As the user, I want finished Tasks to stay finished during a Revision, with any needed changes added as new Tasks, so that the Task history always matches the branch.
94. As the user, I want to cancel an Intent, which destroys its Sandbox and closes its pull requests but keeps its branch and records, so that I can stop for good without losing history.
95. As the user, I want a Cancelled Intent never to be reopened, so that closed means closed.

### Reliability and cost safety

96. As the user, I want a Sandbox that can't be created to turn the Intent red with the Azure error, keeping its slot and state, and offering Retry, so that I can fix the cause (quota, VM size) and continue.
97. As the user, I want Overseer to regularly delete Sandboxes that don't belong to an Active Intent or Repository Setup, so that crashes don't leave expensive VMs running.
98. As the user, I want every Sandbox deleted once it reaches the maximum lifetime, so that there's a hard ceiling on runaway cost.
99. As the user, I want Overseer to continue where it left off after a restart, so that a redeploy or crash doesn't lose orchestration state.
100. As the user, I want each Intent to show the tokens used and VM hours as information, so that I understand what it costs.

## Implementation Decisions

### Shape

- **One executable** (ADR 0004) with commands `install`, `serve`, `reset-password` and `uninstall`. The container image runs `serve`. `install` deploys the image tag that matches its own version, from a public container registry.
- **Portal**: Blazor Web App with interactive Server rendering. Live updates (lights, Task progress, agent streams) flow over Blazor's built-in SignalR connection.
- **Single replica** (min = max = 1). The Portal and orchestration share one process. All orchestration state is kept in PostgreSQL, so a restart resumes from the database.
- **Azure CLI** is the only means of creating and managing Azure resources, through templated bash scripts embedded as resources in the executable.

### Modules

- **Intent Lifecycle**: a deep module that owns Intent state and every transition, guards (slot availability, which states can be paused or cancelled) and side-effect requests (create or destroy a Sandbox, start an agent session). Everything else asks it to transition and never sets state directly.
- **Refinement**: runs the Analyst session, persists the transcript as Rounds, resumes with the transcript, enforces the idle timeout, and receives the written Spec.
- **Spec**: Spec versions, made of prose Markdown sections plus a structured, ordered Task list. Supports editing by the user (new version), sending back to Refinement, and approval (which locks the version).
- **Task Runner**: carries out the Approved Spec's Tasks one at a time (ADR 0001). It starts a fresh agent session per Task with the role prompt, the Approved Spec and the Task, runs verification in the Sandbox, counts attempts, pushes after each verified Task, and runs the final full verification and fix-up Task.
- **Review**: runs the Reviewer session, syncs Review Comments from GitHub (including the user's own), handles triage, creates the "process comments" Task, starts incremental re-review, and on approval merges and cleans up.
- **Sandbox Provider**: the port to the machines. It has two implementations:
  - Azure: a VM per Sandbox, cloud-init, tags, private networking when deployed and a restricted public IP when run locally
  - Fake: offline work and tests

  Its interface covers: create a Sandbox for an owner (Intent or Repository Setup) with the Repositories in scope; run a command and get its exit code and output; start an agent session for a role; destroy; list existing Sandboxes with their owner tags.
- **Agent Session**: an ACP client. It runs one `opencode acp` process per session over SSH, using a fresh key pair per Sandbox. It streams session updates to the Portal, relays user messages and agent questions, and returns the session's structured outcome (for example "done", or a question for the user).
- **GitHub Gateway**: the only component that calls the GitHub API. It opens pull requests, reads review comments and threads, resolves threads with a reply, closes pull requests, squash-merges, deletes branches and creates repositories.
- **Repository Manifest**: reads and validates `.overseer.yml` with the commands `setup`, `build` and `test`, and an optional Compose file path (ADR 0003).
- **Sandbox Reconciler**: a background service that runs at startup and periodically. It compares Sandboxes in Azure (by owner tag) with the database, deletes orphans, and deletes Sandboxes that exceed the maximum lifetime.
- **Settings & Secrets**: settings in PostgreSQL. Secrets in Key Vault through the Managed Identity, or locally through the developer's Azure login or a local secrets file. Configuration is layered: environment variables, then local config file, then defaults.
- **Installer**: the `install`, `uninstall` and `reset-password` commands.

### Intent states and transitions

| From | To | Trigger |
|---|---|---|
| Draft | Refining | User starts (requires a free slot) |
| Draft | *(deleted)* | User deletes |
| Refining | AwaitingSpecApproval | Analyst writes the Spec |
| AwaitingSpecApproval | Refining | User sends the Spec back with comments |
| AwaitingSpecApproval | Approved | User approves |
| AwaitingSpecApproval | Implementing | User chooses "Approve & start" (requires a free slot) |
| Approved | Implementing | User starts (requires a free slot) |
| Implementing | InReview | All Tasks done, final verification passes, pull requests opened |
| InReview | Implementing | User processes accepted comments |
| InReview | Done | User approves and every pull request is merged |
| Refining / Implementing / InReview | Paused | User pauses, or Refinement idle timeout |
| Paused | the state it was paused from | User resumes (requires a free slot) |
| Paused | Refining | User starts a Revision (requires a free slot) |
| Any state except Draft, Done and Cancelled | Cancelled | User cancels |

- Active states (which occupy a slot) are Refining, Implementing and InReview.
- Needs Attention (orange) means one of these: the Analyst is waiting for answers, an agent asked a question, or Review Comments await triage.
- Failed (red) means one of these: a Failed Task, Sandbox creation failed, or a merge failed.
- Task states are Pending, Running, Done and Failed. The runner always takes the first Pending Task.

### Persistence

- PostgreSQL holds Intents, Repositories in scope, Refinement Rounds and transcript, Spec versions with their Tasks, Task attempts and verification output, agent session logs, Review Comments with triage decisions, Sandboxes (owner, Azure resource, created at), Settings, Configured Repositories, Working Time per state, and token and VM-hour usage.
- Schema migrations are applied at startup.
- The user's credential hash is not in PostgreSQL but in configuration (ADR 0005).
- No cross-Intent memory is stored.

### GitHub interaction

- Agents use git and the `gh` CLI inside the Sandbox, with the fine-grained token supplied through a credential helper. The Reviewer posts its review, including screenshots, with `gh pr comment --attach`. That needs `gh` 2.99 or newer.
- Overseer uses the GitHub API (through the GitHub Gateway) to open pull requests, sync comments, resolve threads, close and merge pull requests, delete branches and create repositories.

### Azure resources created by install

- **Overseer resource group:** a Container Apps environment on a VNet, the Container App, PostgreSQL Flexible Server (Burstable tier, Entra authentication), Key Vault, and a user-assigned Managed Identity.
- **Sandbox resource group:** the Managed Identity has Contributor rights here and only here.
- Defaults: region westeurope and a name prefix, both overridable with CLI flags.

### Sandbox contents

- Base cloud-init installs Docker, git, `gh` 2.99 or newer, OpenCode, and Node with Playwright and browsers.
- On creation, every Repository in scope is cloned, its manifest `setup` command is run, and its Compose dependencies are started.
- Each VM is tagged with its owner ID.

### Agent configuration

- Settings hold an editable `opencode.json` (including the model per role: Analyst, Designer, Developer, Reviewer). Provider tokens are kept in Key Vault and injected into Sandboxes.

### Repository Setup

- Asks for the purpose and tech stack, creates the GitHub repository through the Gateway, then runs a one-off agent session in a temporary Sandbox. The session generates the README, `AGENTS.md` and `.overseer.yml` and pushes them directly to main.
- The new repository is registered as a Configured Repository.

### Authentication

- ASP.NET Core cookie authentication for one user, with lockout after repeated failed logins. Only the password hash is stored.

## Testing Decisions

- **What makes a good test:** it exercises externally observable behaviour through the highest available seam (state transitions, what's visible in the Portal, commands sent to Sandboxes, calls made to GitHub), never internal classes or private state. Tests read like the user stories above.
- **Proposed seams** (for confirmation):
  1. **Sandbox Provider.** This is the primary seam, and it already exists by design for offline development (ADR 0004). The fake provider scripts agent sessions (for example "agent says done", "agent asks a question") and command results (for example "tests fail twice, then pass"). It covers Refinement, the Task Runner, verification and the Reviewer without any VM.
  2. **GitHub Gateway.** Fake pull requests, comments and merge outcomes, including a failing merge.
  3. **Clock.** Needed for the idle timeout, maximum lifetime and Working Time.
- **Orchestration tests:** the application runs in-process against a real PostgreSQL (Testcontainers), driven through its application commands (start, approve, pause, resume, revise, cancel, triage, approve review) with the fakes above. These cover the transition table, slot limits, the attempts limit, pause/resume/Revision behaviour, the Needs Attention and Failed lights, and Done only after all merges.
- **Repository Manifest:** parsing and validation tested directly with sample files, including a missing manifest and an invalid one.
- **Sandbox Reconciler:** tested against the fake provider with orphaned and expired Sandboxes.
- **Portal:** a small number of browser end-to-end tests (Playwright) against `overseer serve` with the fake provider, covering the main path from creating an Intent to Done.
- **Azure Sandbox Provider and Installer:** an opt-in smoke test against a real subscription (install, create a Sandbox, run a command, open an ACP session, destroy, uninstall), not part of the default test run.
- **Prior art:** none. The repository is greenfield.

## Out of Scope

- Memory or learning carried across Intents (repository knowledge goes into `AGENTS.md` through normal Tasks).
- Multiple users, user management, roles and password change from the Portal.
- Dependencies between Intents.
- Running Tasks in parallel within an Intent (ADR 0001).
- A Tester role. Developers write tests, and the Reviewer exercises the app.
- An approval step for the Designer's output.
- Queueing Intents to start automatically, or picking up Backlog work automatically.
- Email or push notifications.
- Cost caps or automatic cutoffs per Intent.
- Pre-built VM images, keeping paused Sandboxes, and automatic retry of Sandbox provisioning.
- Custom domains for the Portal.
- GitHub Enterprise Server.
- An Intent template. Intents are free text.

## Further Notes

- **Proof-of-concept risks to settle first:**
  - `gh pr comment --attach` with a fine-grained token. The release notes only confirm OAuth tokens and classic tokens. The fallback is committing screenshots to an orphan branch and embedding absolute `/raw/` URLs.
  - Whether a fine-grained token can create repositories for the chosen owner, as Repository Setup needs.
  - OpenCode's ACP mode over an SSH stdio channel, including streaming and agent questions.
- VM provisioning plus cloud-init will take minutes. The Portal should show it as an explicit phase, so a slow start isn't mistaken for a hang.
- The Spec's structured Task list is what the Task Runner executes. The prose sections are what the Reviewer checks against, and both are part of the locked Approved Spec.
