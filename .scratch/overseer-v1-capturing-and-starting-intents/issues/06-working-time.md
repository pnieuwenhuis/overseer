# 06: Working Time

**What to build:** Every state transition of an Intent is recorded with its time, from the injected `TimeProvider`. The Backlog and the detail page show each Intent's Working Time: the total time it has spent in Active states. Time spent as a Draft, awaiting approval, Approved or Paused doesn't count. Covers user story 39.

The transition history is what later tickets (pause, resume, Revision) rely on for Working Time; nothing else derives it.

**Blocked by:** 04 (Start an Intent, with Active Intent slots)

**Status:** ready-for-agent

- [ ] Every transition the Intent Lifecycle makes is stored with its time; the history survives a restart
- [ ] Working Time is the sum of the periods spent in Active states, up to now for an Intent that is still active
- [ ] The Backlog and the detail page show Working Time in a readable form (for example "2 h 15 min"); a Draft shows none
- [ ] A displayed Working Time of an Active Intent keeps advancing without a reload
- [ ] Tests advance a fake clock instead of waiting
- [ ] The schema change is an EF Core migration applied at startup
- [ ] Covered by tests through the application
