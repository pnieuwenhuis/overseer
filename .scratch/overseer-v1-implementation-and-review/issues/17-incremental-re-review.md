# 17: Incremental re-review

**What to build:** Review cycles stay focused. After comments are processed, the Reviewer looks only at the changes since its last review: Overseer records which commit of each pull request was reviewed, and tells the new session the range to review, the comments that were processed, and those already dismissed, so that it doesn't raise them again. Covers user story 85.

A pull request opened since the last review is reviewed in full. One without new commits isn't reviewed again, and when none has any, no session starts. A failed review doesn't count as the last review.

**Blocked by:** 16 (Process comments)

**Status:** ready-for-agent

- [ ] The reviewed commit of each pull request is recorded when a review succeeds
- [ ] The next review's first message gives each pull request's range of new commits, the processed comments and the dismissed ones
- [ ] A new pull request is reviewed in full, and an unchanged one is left out
- [ ] With no new commits at all, no Reviewer session starts and the Intent shows orange
- [ ] After a failed review, Retry covers everything since the last successful one
- [ ] Earlier Review Comments and their decisions remain listed
- [ ] Covered by tests through the application
