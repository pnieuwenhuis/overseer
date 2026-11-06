# 02: Rounds: the Analyst asks, the user answers

**What to build:** The Analyst questions the user in numbered Rounds. Each question comes with the Analyst's suggested answer. When the Analyst ends its turn with a Round, the Intent Needs Attention (orange light), and its detail page shows the Round as a form. For each question the user can write their own answer, choose "as suggested", or leave it blank. "As suggested" and blank both record the suggested answer as an Analyst's Choice. Submitting sends the answers to the same Analyst session, and the light turns green again while the Analyst works. Rounds and answers are saved as the Refinement transcript. The user can leave and come back without losing anything, and earlier Rounds stay readable on the detail page. Covers user stories 44, 45 and 51.

Decided: the Analyst hands in a Round by writing a structured file at a fixed path in its Sandbox, outside the repositories, and ending its turn. Overseer reads the file through the Sandbox Provider and validates it. An invalid Round is not shown to the user. Overseer sends the validation errors back to the Analyst, which tries again. The Spec in 03 uses the same mechanism, so build it to be shared. The Analyst's instructions describe the format.

The Analyst decides how many Rounds it needs (story 48). Overseer never ends Refinement itself.

**Blocked by:** 01 (Refinement launches the Analyst in a fresh Sandbox)

**Status:** ready-for-agent

- [ ] A turn that ends with a valid Round shows its questions numbered within the Round, each with its suggested answer, and the Round itself numbered (Round 1, Round 2, ...)
- [ ] While a Round awaits answers the Intent's light is orange (Needs Attention), in the Backlog and on the detail page
- [ ] Each question can be answered with free text, "as suggested", or left blank; "as suggested" and blank are recorded as Analyst's Choices with the suggested answer
- [ ] Submitting the answers sends them to the same Analyst session, with each Analyst's Choice marked as such, and turns the light green
- [ ] An invalid Round is never shown; the Analyst gets the validation errors and its next turn is handled as usual
- [ ] The transcript (every Round with its questions, suggestions and answers) is saved in PostgreSQL and shown on the detail page, also after leaving the page and after a restart of `serve`
- [ ] Answers can't be submitted twice for the same Round, and not for a Round that isn't the current one
- [ ] Covered by tests through the application with the fake Sandbox Provider
