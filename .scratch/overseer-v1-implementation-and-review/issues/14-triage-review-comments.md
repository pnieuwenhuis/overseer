# 14: Triage Review Comments

**What to build:** The user decides what gets changed. Each Review Comment can be accepted or dismissed, with an optional note. A dismissal is applied to GitHub through the GitHub Gateway: the note is posted as a reply and the thread is resolved. The Portal shows each comment's decision and how many still await one. Covers user stories 81 and 83.

A dismissal without a note resolves the thread without a reply. A general pull request comment can't be resolved on GitHub, so dismissing it only posts the note, and nothing when there is none. Replies Overseer posts carry a hidden marker, so they are never read back as comments.

A dismissal is final, as GitHub already shows it. An acceptance can still be changed until the comment is processed.

**Blocked by:** 12 (The Reviewer reviews the pull requests)

**Status:** ready-for-agent

- [ ] Each untriaged Review Comment offers Accept and Dismiss, with an optional note, also while other comments are untriaged
- [ ] Decisions and notes are kept in PostgreSQL and shown with the comment
- [ ] Dismissing a thread posts the note as a reply, if any, and resolves it; dismissing a general comment posts only the note
- [ ] When GitHub refuses, the comment stays as it was and the user sees GitHub's error
- [ ] An accepted comment can be dismissed later; a dismissed one can't be changed
- [ ] The Intent Needs Attention while comments await triage, and the detail page says how many
- [ ] Covered by tests through the application, and the GitHub Gateway contract tests cover replying and resolving
