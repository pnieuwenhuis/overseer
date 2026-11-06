# 09: Azure Sandboxes clone the Repositories in scope; Refinement Sandboxes can't push

**What to build:** An Azure Sandbox actually holds the Repositories in scope it is created with. Each one is cloned using the GitHub fine-grained token, supplied through a git credential helper, never written into a remote URL or a file the agent is told about. `gh` is authenticated the same way. A Sandbox for Refinement has its push access taken away once cloning is done, so the Analyst can read, build and test the code but cannot push to any repository. The prompt and `opencode.json` can't override this. Covers user story 47 as a hard guarantee, and the "containing the Repositories in scope" part of story 43 for real Sandboxes.

Out of scope: running the manifest's `setup` command and starting Compose dependencies. Those come with implementation (story 63).

How push is prevented is up to the implementer. One option is to remove the credential helper and `gh` authentication after cloning. The guarantee has to hold even for an agent that tries `git push` or `gh` directly. The Sandbox Provider interface has to let its caller say whether a Sandbox may push. The fake provider records that choice so orchestration tests can check it.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Creating an Azure Sandbox clones each Repository in scope into a known place, on its default branch, and only reports ready once cloning has finished
- [ ] A clone failure (bad token, missing repository) fails the creation with a message naming the repository and GitHub's error
- [ ] The token never appears in a remote URL, in the command output Overseer logs, or in PostgreSQL
- [ ] In a Sandbox created without push access, `git push` and `gh` write operations fail for lack of credentials; fetching what was cloned still works
- [ ] The fake Sandbox Provider records whether each Sandbox was created with push access, and Refinement (01) asks for none
- [ ] The opt-in Azure provider contract test covers cloning and the denied push
