# 08: An agent asks you a question

**What to build:** An agent that is truly stuck asks instead of guessing. When a Task's agent ends its turn with a question, the Intent turns orange and the Portal shows the question with a place to answer it. The answer goes to the same session and the Task carries on. Covers user story 75.

This replaces ticket 02's behaviour of failing on a question. The Intent stays Implementing and keeps its slot and Sandbox while it waits; there is no idle timeout here, as that is Refinement's. The agents' instructions say to ask only when they can't reasonably decide themselves.

**Blocked by:** 02 (Tasks run in order), 03 (Verify, then push)

**Status:** ready-for-agent

- [ ] A turn ending in a question turns the Intent orange and shows the question on the detail page, with the running Task
- [ ] The answer is sent to the same session, the light turns green again and the Task continues to verification
- [ ] A question and its answer use no verification attempt, also when the question follows a failed verification
- [ ] An answer is taken only once
- [ ] The question and answer are kept with the Task
- [ ] Covered by tests through the application
