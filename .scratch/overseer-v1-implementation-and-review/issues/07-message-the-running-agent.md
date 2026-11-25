# 07: Message the running agent

**What to build:** While a Task runs, the user can send its agent a message to steer it. Covers user story 74.

Decided: an agent session takes one message per turn, so the message is delivered as soon as the current turn ends, before verification, as the next turn. Overseer doesn't interrupt the turn. The Portal shows the message as waiting until it is delivered. A message that arrives while verification runs is delivered before the Task is marked Done, and verification runs again after the turn it starts.

**Blocked by:** 03 (Verify, then push), 06 (Follow a running Task)

**Status:** ready-for-agent

- [ ] The stream of a running Task offers a message box; no other Task does
- [ ] A message is delivered to the same session when its turn ends, and verification waits for the turn it starts
- [ ] Several messages sent during one turn are all delivered, in order
- [ ] A message sent during verification is delivered before the Task is Done, and the work is verified again
- [ ] A turn started by a message uses no verification attempt
- [ ] The message shows in the stream, as waiting and then as delivered; one that can no longer be delivered is reported to the user, not silently dropped
- [ ] Covered by tests through the application
