# Tasks within an Intent run sequentially

The original concept had agents working on an Intent's Tasks in parallel where possible. We decided that an Intent runs one Task at a time, even when Tasks touch different repositories, in a single shared Sandbox where each Task gets a fresh agent session. Parallel agents on one branch would push conflicting changes, and a paused or failed agent's half-finished work would break the build for the others. Parallelism comes from running multiple Active Intents instead, at the cost of a slower single Intent.

## Consequences

- Tasks are an ordered list, not a dependency graph. There is no "blocked-by".
- After each verified Task its work is pushed straight to the Intent branch. There are no per-Task branches.
- A Task may span several repositories, since every Repository in scope sits in the same Sandbox.
