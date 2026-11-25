# 13: The Reviewer exercises the app

**What to build:** The review covers behaviour, not just code. The Reviewer starts the application in the Sandbox, exercises the API endpoints and UI changes the Spec lists, takes screenshots with the headless browser and posts them on the pull request, in the way ticket 11 settled. What doesn't work as specified becomes a Review Comment, as does an application that won't start. When the Spec lists no API or UI changes, the review summary says nothing was exercised and why. Covers user stories 79 and 80 (screenshots).

How to start the application comes from the repository's own guidance (ADR 0003).

**Blocked by:** 11 (Settle screenshot attachment), 12 (The Reviewer reviews the pull requests)

**Status:** ready-for-agent

- [ ] The Reviewer's instructions tell it to start the application, exercise every API endpoint and UI change in the Approved Spec, and screenshot the UI with the headless browser
- [ ] Screenshots are posted on the pull request of the repository they concern, using the way ticket 11 settled, carrying the Reviewer's marker
- [ ] Screenshot posts aren't listed as Review Comments to triage
- [ ] Screenshots don't end up on the Intent branch or the default branch
- [ ] Posting works in a real Azure Sandbox, checked in an opt-in run
- [ ] Covered by tests through the application with the fakes
