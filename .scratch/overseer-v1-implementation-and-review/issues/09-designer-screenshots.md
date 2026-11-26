# 09: Designer screenshots

**What to build:** The user can see a design before Developers build on it. A Designer Task produces working UI code with placeholder data, and hands in screenshots of it, which the Portal shows with the Task. There is no approval step: the next Task starts as usual. Covers user story 65.

The Designer's instructions ask for the UI code, placeholder data and screenshots taken with the headless browser in the Sandbox, handed in outside the repositories, as agents hand in other things. The Sandbox Provider only takes text files from a Sandbox so far, so it has to take binary files too.

A Designer Task without screenshots isn't done: the agent is told what is missing, and that uses a verification attempt.

**Blocked by:** 03 (Verify, then push)

**Status:** done

- [x] A Designer session's instructions ask for working UI code with placeholder data and for screenshots
- [x] The Sandbox Provider takes binary files from a Sandbox unchanged, in both the fake and Azure, covered by the contract tests
- [x] The screenshots handed in are kept and shown with the Task in the Portal, also after the Sandbox is gone
- [x] Screenshots are not committed to the Intent branch
- [x] A Designer Task that hands in none, or something that isn't an image, is told so and uses an attempt
- [x] Covered by tests through the application
