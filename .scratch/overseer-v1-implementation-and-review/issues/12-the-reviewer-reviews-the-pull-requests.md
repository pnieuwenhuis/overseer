# 12: The Reviewer reviews the pull requests

**What to build:** When an Intent enters In Review, a Reviewer agent session starts in the Intent's Sandbox. It reviews the pull requests against the Approved Spec, the Review Guidelines and each repository's agent guidance (`AGENTS.md`), and posts its comments on GitHub. Overseer reads them back through the GitHub Gateway and lists them in the Portal as Review Comments, per pull request. Covers user stories 78, 80 (comments) and 81 (seeing them).

Decided: the Reviewer posts with the same token as the user, so everything it posts carries a hidden marker. A remark about specific lines is a review thread on those lines, which can later be resolved; a general remark is a pull request comment. It also posts a summary of its review, even with nothing to remark; the summary isn't a Review Comment. A session that ends without having posted it is told so, and after a few tries the review has failed.

The Reviewer doesn't change or push code. A Reviewer that asks a question is told to decide for itself and to raise its doubt as a comment.

The Intent is green while the Reviewer works and orange afterwards. A failed review turns it red, with Retry. A stopgap until ticket 19: a review that a restart of `serve` interrupts is marked failed.

**Blocked by:** 05 (Final verification and pull requests)

**Status:** ready-for-agent

- [ ] Entering In Review with pull requests starts one Reviewer session in the Intent's Sandbox, with none when there are no pull requests
- [ ] Its first message holds the role's instructions, the Approved Spec, the Review Guidelines as saved at that moment, the pull requests, and where each repository's agent guidance is
- [ ] After the review, the marked comments on every pull request are stored as Review Comments, each once, with their file and line where they have one
- [ ] The Portal lists the Review Comments per pull request, updating live, with a link to each on GitHub
- [ ] The light is green while the Reviewer works and orange once it is done
- [ ] A session that can't start, breaks off or posts no summary turns the Intent red with the reason; Retry reviews again, in a fresh Sandbox from the Intent branch if the old one is gone
- [ ] The Reviewer's session stream is kept and can be read like a Task's, if ticket 06 is done
- [ ] Covered by tests through the application with the fakes, and the GitHub Gateway contract tests cover reading comments and threads
