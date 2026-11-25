# 02: Tasks run in order

**What to build:** Once the Sandbox is ready, the Task Runner carries out the Approved Spec's Tasks one at a time (ADR 0001). The Intent gets its own Task list, created from the Approved Spec, each Task Pending, Running, Done or Failed. The runner always takes the first Pending Task and starts a fresh agent session for that Task's role (Designer or Developer), whose first message holds the role's instructions, the Approved Spec and the Task. The instructions tell a Developer to always include unit and integration tests for its own work, and tell every agent to leave pushing to Overseer. The user sees the Task list with each Task's role and state, live. Covers user stories 64 and 66, and the Task list of story 72.

The Intent's Tasks are kept apart from the Approved Spec's, which stays locked: later tickets add Tasks and edit them.

Until ticket 03, a Task is Done when its agent's turn ends as done. Until ticket 08, a question from the agent fails the implementation. A session that can't start or breaks off turns the Intent red with the reason; Retry runs the same Task again in a fresh session. Such a Task is not a Failed Task, which is one that doesn't pass verification (ticket 03). After the last Task the Intent stays Implementing until ticket 05.

**Blocked by:** 01 (Implementing prepares a Sandbox)

**Status:** ready-for-agent

- [ ] The Intent's Tasks are created from the Approved Spec in its order, all Pending, and are kept in PostgreSQL
- [ ] Only one Task runs at a time, always the first Pending one, and each gets its own agent session in the Task's role, stopped when the Task ends
- [ ] The first message of a session holds the role's instructions, the Approved Spec and the Task, including the repositories it touches
- [ ] The Developer instructions require unit and integration tests for the Task's work
- [ ] The detail page shows every Task with its role and state, updating live, and the Intent shows a green light while a Task runs
- [ ] A session that fails to start or breaks off turns the Intent red with the reason, and Retry runs that Task again in a new session
- [ ] Covered by tests through the application with scripted agent sessions
