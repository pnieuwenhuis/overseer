# 16: Process comments

**What to build:** "Process comments" has an agent address the accepted Review Comments in one go. It creates one Developer Task holding every accepted, unprocessed comment with the user's notes, and moves the Intent back to Implementing. The Task Runner carries it out like any Task, then runs the final verification and returns the Intent to In Review, where the pushed work shows on the pull requests. Covers user story 84.

Offered when at least one accepted comment is unprocessed and the Reviewer isn't working. Untriaged comments are left as they are. Comments are synced first, so the latest are included. The Intent already holds a slot and keeps it. The Sandbox is the same one, brought back to the Intent branch first, since the Reviewer may have left changes or the application running.

A repository that changes only now gets its pull request; existing pull requests are not opened again. Until ticket 17, the Reviewer reviews everything again.

**Blocked by:** 14 (Triage Review Comments)

**Status:** ready-for-agent

- [ ] "Process comments" is offered only when there is an accepted unprocessed comment and no agent is working
- [ ] It adds one Developer Task with every such comment, its location and the user's note, touching the repositories they are on, and moves the Intent to Implementing through the Intent Lifecycle
- [ ] The Task starts on a clean checkout of the Intent branch, and is verified, retried and recovered like any Task
- [ ] Comments in the Task are marked processed and never go into another one
- [ ] After final verification the Intent returns to In Review, with a pull request for each newly changed repository and none opened twice
- [ ] Pressed twice at once, it creates one Task
- [ ] Covered by tests through the application
