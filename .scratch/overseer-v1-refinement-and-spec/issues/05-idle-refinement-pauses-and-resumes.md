# 05: Idle Refinement pauses; Resume continues from the transcript

**What to build:** A Refinement waiting on the user doesn't keep a machine running indefinitely. A Round can stay unanswered for longer than the Refinement idle timeout (setting, default 30 minutes). When it does, the Intent moves from Refining to Paused: its Sandbox is destroyed, its Analyst session stopped and its slot freed. A Paused Intent offers Resume, which needs a free slot. Resuming moves it back to Refining in a fresh Sandbox. A new Analyst session gets the transcript so far: the Intent's text, every Round with its answers and Analyst's Choices, and the unanswered Round if there was one. The user doesn't have to repeat anything. Covers user stories 52 and 53. It also covers story 99 for Refinement.

The resume mechanism is the one the rest of the work reuses: starting a fresh Analyst session from the saved transcript. Sending a Spec back (07) and a Revision use it too. A restart of `serve` while an Intent is Refining recovers the same way: the old Analyst session is gone, so Refinement continues in a fresh Sandbox from the transcript. That old Sandbox is destroyed.

The idle timeout runs on the clock seam (a `TimeProvider`), so tests don't wait. A user-initiated Pause button, and the Paused → Refining Revision, come with the pause, resume, Revision and cancel tickets.

Decided: an unanswered Round that was paused stays answerable after Resume. The resumed Analyst is told which Round is still open, rather than asking it again.

**Blocked by:** 02 (Rounds: the Analyst asks, the user answers)

**Status:** ready-for-agent

- [ ] A Round left unanswered for longer than the idle timeout moves the Intent to Paused through the Intent Lifecycle, destroys its Sandbox and frees its slot; time spent while the Analyst works doesn't count as idle
- [ ] Changing the idle timeout applies to Refinements already waiting
- [ ] A Paused Intent takes no slot and has no light (new test for story 42); its Working Time stops accruing
- [ ] Resume is offered on a Paused Intent that was Refining, and is disabled with the same explanation as Start when all slots are taken
- [ ] Resuming moves the Intent back to Refining in a fresh Sandbox, and the new Analyst session's first message holds the full transcript
- [ ] After `serve` restarts, each Refining Intent continues in a fresh Sandbox from its transcript, and Sandboxes left from before the restart are destroyed
- [ ] Covered by tests through the application with the fake Sandbox Provider and a controllable clock
