# 10: ACP Agent Session over SSH

**What to build:** Agents run in real Azure Sandboxes. This is the spec's **Agent Session** module: an ACP client that runs one `opencode acp` process per session over SSH, with the Sandbox's own key pair. It configures the process with the harness configuration and the environment it's given (model provider tokens). It runs the role's OpenCode agent with that agent's model. The session's updates stream out as they happen. A turn ends with a structured outcome: done, or a question for the user. The Azure Sandbox Provider's agent sessions stop refusing, so Refinement and Repository Setup work end to end against Azure. This also settles one of the spec's proof-of-concept risks: OpenCode's ACP mode over an SSH stdio channel, including streaming and agent questions.

How an agent asks the user a question over ACP is for the implementer to settle and record in the code. It has to map onto the `AskedQuestion` outcome the orchestration already uses. If ACP can't carry a question cleanly, write down the finding and the fallback chosen, as an ADR if it constrains later work.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Starting an agent session in an Azure Sandbox runs `opencode acp` over SSH with the given harness configuration and environment, in the given role's agent
- [ ] Sending a message starts a turn; the agent's updates stream as they happen, and the turn ends with Done or AskedQuestion
- [ ] An answer sent to the same session continues the conversation where it left off
- [ ] A dropped SSH connection or exited process ends the turn with `AgentSessionFailedException` and a message saying what happened
- [ ] Disposing the session stops the agent process, also in the middle of a turn
- [ ] Provider tokens are never logged or written to PostgreSQL
- [ ] The agent-session part of the Sandbox Provider contract suite, skipped for Azure until now, passes against a real subscription as an opt-in test
