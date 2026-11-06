# 08: Approve the Spec; Approve & start; start an Approved Intent later

**What to build:** The user approves the Spec version they're looking at. Approval locks it as the Approved Spec, the fixed contract for implementation and review. If a slot is free, the button reads "Approve & start" and moves the Intent straight to Implementing. Otherwise it reads "Approve" and moves the Intent to Approved, which takes no slot. An Approved Intent offers Start, which needs a free slot and moves it to Implementing. Covers user stories 60, 61 and 62. It also covers story 42 for Approved.

Implementing does nothing yet, as Refining did after ticket 04 of the capturing-and-starting tickets. The Task Runner hooks into this transition later. Until then an Implementing Intent shows a green light and holds its slot.

Decided: only the latest Spec version can be approved. Approving an earlier version would quietly undo later edits. If an earlier one is wanted, the user re-applies it as an edit.

**Blocked by:** 06 (Edit the Spec and reorder its Tasks)

**Status:** ready-for-agent

- [ ] An Intent awaiting Spec approval offers "Approve & start" when a slot is free and "Approve" when none is, with an explanation of why it can't start
- [ ] Approving records the latest Spec version as the Approved Spec; afterwards the server refuses any edit to it, and Send back is no longer offered
- [ ] "Approve & start" moves the Intent to Implementing through the Intent Lifecycle; if the last slot was taken in the meantime, it is refused and nothing is approved
- [ ] "Approve" moves the Intent to Approved, which takes no slot (new test for story 42) and has no light
- [ ] An Approved Intent offers Start, disabled with the usual explanation when all slots are taken; Start moves it to Implementing
- [ ] The detail page of an Approved or Implementing Intent shows the Approved Spec and which version it is
- [ ] Covered by tests through the application
