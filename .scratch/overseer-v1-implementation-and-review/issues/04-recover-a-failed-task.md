# 04: Recover a Failed Task

**What to build:** A Failed Task offers two ways on. Retry runs it again with a fresh agent session and a fresh set of verification attempts, telling the agent what failed last time. Edit & retry lets the user first change the Task's title, description, role or repositories. Covers user story 70, except pausing the Intent for a Revision, which comes with the pause, resume, Revision and cancel tickets.

Editing changes the Intent's Task only. The Approved Spec stays as approved, and the Reviewer keeps reviewing against it. Finished Tasks can't be edited or retried.

The work the failed attempts left in the Sandbox stays there for the new agent to continue from.

**Blocked by:** 03 (Verify, then push)

**Status:** ready-for-agent

- [ ] A Failed Task offers Retry and Edit & retry, and no other Task does
- [ ] Retry sets the Task back to Pending with its attempts counted afresh, clears the red light and runs it in a new session that is told the last verification output
- [ ] Edit & retry saves a changed title, description, role and repositories (only Repositories in scope, at least one) and then retries
- [ ] The Approved Spec is unchanged by an edit, and the earlier attempts remain visible
- [ ] Covered by tests through the application
