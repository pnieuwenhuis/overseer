# 11: Settle screenshot attachment

**What to build:** An answer to the spec's open proof-of-concept risk: how the Reviewer puts screenshots on a pull request using Overseer's fine-grained GitHub token. Try `gh pr comment --attach` with `gh` 2.99 or newer against a private throwaway repository; its release notes only confirm OAuth and classic tokens. If it doesn't work, settle the fallback instead: commit the screenshots to an orphan branch and embed absolute `/raw/` URLs, and confirm that they render in a private repository's pull request.

The outcome is a decision that ticket 13 builds on, written down with the evidence, as an ADR if it constrains later work. If the fallback is chosen, say what happens to the orphan branch when the Intent is Done.

Needs a fine-grained token and a throwaway repository from the human. The token is not written into the repository or any log.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] `gh pr comment --attach` is tried with a fine-grained token on a private repository, and the result is recorded, with `gh`'s error if it fails
- [ ] The chosen way is shown to render a screenshot in a pull request comment of a private repository
- [ ] The token permissions it needs are recorded, and stated wherever Overseer tells the user which permissions to give the token
- [ ] The decision is written down for ticket 13
