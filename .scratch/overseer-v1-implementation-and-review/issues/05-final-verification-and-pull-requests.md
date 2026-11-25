# 05: Final verification and pull requests

**What to build:** Pull requests are only opened for green code. Once all Tasks are Done, Overseer runs the full build and tests on the Intent branch. When they fail, it adds a fix-up Task. When they pass, it opens one pull request per repository that actually changed, none for unchanged ones, and the Intent moves to In Review. Covers user stories 76 and 77.

Decided: final verification covers every Repository in scope. A repository without a Repository Manifest is skipped when unchanged, and fails verification when changed.

Decided: the fix-up Task is a Developer Task, appended to the Intent's Tasks, holding the failing output and touching the failing repositories. Its verification is the full verification itself, so it either passes or becomes a Failed Task after the allowed attempts. No second fix-up Task is added.

Opening pull requests goes through the GitHub Gateway. A pull request's title comes from the Intent and its description from the Spec's summary. When no repository changed, the Intent moves to In Review without pull requests. The Sandbox stays for the review.

Decided: an Intent In Review is orange whenever no agent is working on it, since only the user can move it on. Until ticket 12 that is always.

**Blocked by:** 03 (Verify, then push)

**Status:** done

- [x] After the last Task, `build` and `test` run for every Repository in scope; a changed repository without a manifest fails this
- [x] A failing final verification appends one fix-up Developer Task with the output, which runs like any Task and is verified by the full verification
- [x] A fix-up Task that doesn't pass within the allowed attempts is a Failed Task, recoverable as in ticket 04, and no further fix-up Task is added
- [x] A passing final verification opens a pull request from the Intent branch to the default branch for each changed repository, and none for the others
- [x] The Intent moves to In Review through the Intent Lifecycle, keeps its slot, Sandbox and Working Time, and shows an orange light
- [x] The detail page links to each pull request
- [x] A pull request that can't be opened turns the Intent red with GitHub's error; Retry opens only the missing ones
- [x] Covered by tests through the application with the fake GitHub Gateway, and the GitHub Gateway contract tests cover opening a pull request
