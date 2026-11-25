# 19: Review survives a restart

**What to build:** A redeploy or crash doesn't strand an Intent In Review. After `serve` restarts, whatever was under way continues: an interrupted review runs again, comment processing carries on, and an interrupted approval finishes merging. Covers user story 99 for review.

Decided, as for implementation: old Sandboxes are destroyed, and a fresh one is prepared from the Intent branch. An Intent In Review that only waits on the user gets its new Sandbox when an agent next needs one, so it costs nothing until then.

An interrupted review is told which comments already exist, so it doesn't post them again. This replaces ticket 12's stopgap of marking an interrupted review failed.

**Blocked by:** 10 (Implementation survives a restart), 15 (Your GitHub comments appear), 17 (Incremental re-review), 18 (Approve and merge)

**Status:** ready-for-agent

- [ ] A review interrupted by a restart runs again in a fresh Sandbox, over the same range, without duplicating comments already posted
- [ ] "Process comments" interrupted by a restart carries on, and its comments end up in exactly one Task
- [ ] An approval interrupted by a restart finishes: pull requests merged before it aren't merged again, and the Intent becomes Done or shows the failed merge
- [ ] An Intent In Review with no work under way gets no Sandbox until one is needed, and "Process comments" and Retry then prepare one
- [ ] Syncing of GitHub comments resumes
- [ ] Failed reviews and merges stay red after a restart
- [ ] Covered by tests through the application, restarting it with the fakes
