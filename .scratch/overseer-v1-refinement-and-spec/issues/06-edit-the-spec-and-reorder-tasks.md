# 06: Edit the Spec and reorder its Tasks

**What to build:** While an Intent awaits Spec approval, the user can make small corrections themselves. They can edit the prose sections and edit any Task's title, description, role and repositories. They can add Tasks, remove Tasks and reorder them. Saving creates a new Spec version, and the earlier ones are kept. The detail page shows the latest version and lets the user look at earlier versions, each marked as written by the Analyst or edited by the user. Covers user stories 57 and 58.

An edited Spec has to pass the same validation as one written by the Analyst (03 and 04). An invalid edit is refused with the reasons, and nothing is saved.

**Blocked by:** 03 (The Analyst writes the Spec)

**Status:** done

- [x] On an Intent awaiting Spec approval, the user can edit the prose sections and every Task's title, description, role and repositories, and add and remove Tasks
- [x] The user can move a Task up or down in the ordered list
- [x] Saving creates a new Spec version; earlier versions stay readable, each showing whether the Analyst wrote it or the user edited it
- [x] An edit that fails Spec validation is refused with the reasons, and no version is created
- [x] Editing is refused in any state other than AwaitingSpecApproval, also when the Portal is bypassed
- [x] Two saves based on the same version can't both succeed; the second is refused and the user sees the newer version
- [x] Covered by tests through the application
