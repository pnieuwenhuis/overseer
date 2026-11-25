# 06: Follow a running Task

**What to build:** The user can follow progress without reading logs, and read them when they want to. The running Task shows a one-line "currently doing" summary. Opening it shows the live stream of its agent session: what the agent says and the tools it calls, as it happens. The session logs are kept, so a finished Task's sessions can still be read, and leaving the page loses nothing. Covers user stories 72 (the summary) and 73.

An Agent Session's updates are plain text today, mixing fragments of the agent's messages with the titles of its tool calls. Showing a readable stream and a one-line summary likely needs the session to say which is which.

**Blocked by:** 02 (Tasks run in order)

**Status:** ready-for-agent

- [ ] The Task list shows a one-line summary of what the running Task's agent is doing, updating live
- [ ] Opening a Task shows its agent session stream live, from the session's start, with messages readable as whole text and tool calls distinguishable from them
- [ ] Session logs are kept in PostgreSQL per Task, including every session of a retried Task, and can be read after the Task has ended and after a restart
- [ ] Model provider tokens and the GitHub token never appear in a stored or shown log
- [ ] Covered by tests through the application, and the fake and ACP Agent Sessions report updates the same way
