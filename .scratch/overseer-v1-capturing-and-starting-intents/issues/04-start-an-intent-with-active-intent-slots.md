# 04: Start an Intent, with Active Intent slots

**What to build:** The user starts work on a Draft explicitly with Start on its detail page. Starting moves it to Refining and from then on its text and Repositories in scope are locked. At most the configured maximum of Active Intents (setting from ticket 02 of the login-and-settings tickets, default 2) can be active at once. When all slots are taken, Start is disabled and the page explains why and what the user can do. Covers user stories 40, 41, 42 and 35.

This ticket introduces the **Intent Lifecycle** module from the spec: a deep module that owns Intent state, every transition, and the guards on them. Everything else asks it to transition and never sets state directly. For now it wires only Draft → Refining (and the Draft deletion from 03 goes through it too). It defines, in one place, which states are Active (Refining, Implementing, InReview); slot counting uses that definition only.

Refining does nothing yet: launching the Analyst in a Sandbox (story 43) comes with the Refinement tickets, which hook into this transition.

Story 42 can only be partly verified now: AwaitingSpecApproval, Approved and Paused can't be reached yet. This ticket tests that a Draft takes no slot and a Refining Intent does. Each later ticket that makes one of those states reachable adds a test that it frees the slot.

Decided: lowering the maximum below the current number of Active Intents leaves the running ones alone; Start simply stays disabled until a slot is free.

**Blocked by:** 03 (Edit and delete a Draft)

**Status:** done

- [x] A Draft's detail page offers Start; starting moves the Intent to Refining, visible in the Backlog and on the detail page
- [x] Start requires at least one Repository in scope; without one it is disabled with an explanation
- [x] Once started, the Intent's text and Repositories in scope can no longer be edited, and it can no longer be deleted; the server refuses such changes even if the Portal is bypassed
- [x] When the number of Active Intents equals the maximum, Start is disabled and the page says all slots are taken, by which Intents, and that the user can raise the maximum in Settings
- [x] Only Intents in an Active state count towards the maximum; Drafts never do, and a Repository Setup never does (the existing test still passes)
- [x] Two Starts racing for the last free slot can't both succeed; the loser is refused with the same explanation
- [x] Lowering the maximum below the current number of Active Intents leaves them Refining
- [x] The Intent Lifecycle refuses transitions not in the spec's transition table
- [x] Covered by tests through the application
