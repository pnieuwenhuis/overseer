# 15: Your GitHub comments appear

**What to build:** The user can review wherever they prefer. Comments they write on a pull request of an Intent show up in the Portal as Review Comments that are already accepted. While an Intent is In Review, Overseer syncs periodically, and on request. Covers user story 82.

Decided: a comment is the user's when it is written by the GitHub token's user and carries no marker of the Reviewer or Overseer. Line comments, replies in a thread, general pull request comments and the text of a submitted review all count. Comments by anyone else are ignored.

**Blocked by:** 14 (Triage Review Comments)

**Status:** ready-for-agent

- [ ] The user's unmarked comments appear as accepted Review Comments, within one sync interval and without reloading the page
- [ ] A "Sync" action fetches them at once
- [ ] Syncing again adds nothing twice, keeps triage decisions, and takes over a changed text of a comment not yet processed
- [ ] Marked comments and other people's comments are never listed as the user's
- [ ] The interval runs on the clock seam, and syncing stops once the Intent leaves In Review
- [ ] A sync that fails shows GitHub's error and is tried again at the next interval
- [ ] Covered by tests through the application with the fake GitHub Gateway and a controllable clock
