# 10: Implementation survives a restart

**What to build:** A redeploy or crash of Overseer doesn't strand an implementation. After `serve` restarts, each Implementing Intent that hadn't failed carries on by itself. Covers user story 99 for implementation.

Decided: the old Sandbox is destroyed and a fresh one is prepared from the Intent branch. Done Tasks stay Done. The interrupted Task starts over, with a new agent session and its attempts counted afresh; its unpushed work is lost, as is a question it was waiting on. An interrupted final verification or opening of pull requests is done again.

A failed implementation, including a Failed Task, stays failed and gets its new Sandbox when the user retries.

This replaces ticket 01's stopgap of marking interrupted implementations failed.

**Blocked by:** 03 (Verify, then push), 08 (An agent asks you a question)

**Status:** done

- [x] After a restart each interrupted Implementing Intent continues in a fresh Sandbox, and Sandboxes left from before the restart are destroyed
- [x] The first Task that isn't Done runs again from the start; Done Tasks don't run again
- [x] A Task that was pushed but not yet marked Done when Overseer stopped doesn't leave the Intent stuck
- [x] A restart during final verification or while opening pull requests ends with each changed repository having exactly one pull request
- [x] A failed implementation stays red, and its Retry works after the restart
- [x] Covered by tests through the application, restarting it with the fake Sandbox Provider
