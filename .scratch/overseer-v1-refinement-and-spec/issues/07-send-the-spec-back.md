# 07: Send the Spec back to Refinement with comments

**What to build:** When the Spec isn't right, the user sends it back with comments instead of approving it. The Intent moves from AwaitingSpecApproval to Refining. Refinement continues in a fresh Sandbox, using the resume mechanism from 05. The new Analyst session gets the transcript, the latest Spec version (including the user's own edits from 06) and the user's comments. The Analyst may ask further Rounds. The next Spec it hands in becomes a new Spec version, and the Intent awaits approval again. Covers user story 59.

Decided: sending back needs a free slot, because Refining is an Active state. When all slots are taken, "Send back" is disabled with the same explanation as Start.

**Blocked by:** 05 (Idle Refinement pauses; Resume continues from the transcript), 03 (The Analyst writes the Spec)

**Status:** ready-for-agent

- [ ] An Intent awaiting Spec approval offers "Send back" with a required comments field
- [ ] Sending back moves the Intent to Refining through the Intent Lifecycle, takes a slot, creates a fresh Sandbox and starts an Analyst session whose first message holds the transcript, the latest Spec version and the comments
- [ ] "Send back" is disabled, with an explanation, when all slots are taken; the server refuses it too
- [ ] The comments become part of the transcript, shown on the detail page between the Spec version they were about and what followed
- [ ] The Analyst's next Spec is stored as a new version after the existing ones, and the Intent moves to AwaitingSpecApproval again
- [ ] Covered by tests through the application with the fake Sandbox Provider
