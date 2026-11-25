# 18: Approve and merge

**What to build:** The user has the final say. At any time during review they can approve the Intent. Overseer squash-merges every one of its pull requests through the GitHub Gateway and deletes the Intent branch. Only when all are merged is the Intent Done, and then its Sandbox is destroyed. When a merge fails, the Intent turns red with GitHub's error and stays In Review. Covers user stories 86, 87 and 88.

Approving stops a Reviewer that is still working. Untriaged and accepted comments don't block it.

After a failed merge the user sorts out the cause on GitHub and approves again, which merges only what isn't merged yet; a pull request the user merged themselves counts as merged. Once any pull request is merged, "Process comments" is no longer offered, as the Intent can only be finished.

Every Repository in scope ends without the Intent branch, also those without a pull request. An Intent without pull requests becomes Done at once.

**Blocked by:** 12 (The Reviewer reviews the pull requests)

**Status:** ready-for-agent

- [ ] Approve is offered on every Intent In Review, also while the Reviewer works, which it stops
- [ ] Every pull request is squash-merged and its branch deleted; branches in repositories without one are deleted too
- [ ] The Intent moves to Done through the Intent Lifecycle only when all are merged: its Sandbox is destroyed, its slot freed, its Working Time stopped and it is listed as Closed
- [ ] A failed merge turns the Intent red naming the pull request and GitHub's error; the detail page shows which are merged
- [ ] Approving again merges only the rest, and counts pull requests already merged on GitHub
- [ ] A failure to delete a branch is reported but doesn't undo or block Done
- [ ] Approving twice at once merges once, and approving can't run alongside "Process comments" where ticket 16 is done
- [ ] Covered by tests through the application, including a failing merge, and the GitHub Gateway contract tests cover merging and deleting branches
