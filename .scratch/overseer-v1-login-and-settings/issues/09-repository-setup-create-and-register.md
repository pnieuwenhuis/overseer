# 09: Repository Setup: create and register a repository

**What to build:** From the Settings page the user starts a Repository Setup. They state the new repository's owner, name, purpose and tech stack. Overseer creates the GitHub repository through the GitHub Gateway and registers it as a Configured Repository. The purpose and tech stack are kept with the Repository Setup for the agent in 10. A Repository Setup is not an Intent and takes no Active Intent slot. Covers user stories 20 and 22.

Proof-of-concept risk from the spec: whether a fine-grained token can create repositories for the chosen owner. If it can't, GitHub's error is shown to the user as is.

**Blocked by:** 06 (Configured Repositories)

**Status:** done

- [x] The user can start a Repository Setup by giving owner, name, purpose and tech stack; all are required
- [x] The GitHub Gateway creates the repository; the fake Gateway records it, and can be scripted to fail
- [x] On success the new repository appears among the Configured Repositories
- [x] A failure (name taken, token lacks rights) shows GitHub's error, and nothing is registered
- [x] The Repository Setup, with its purpose and tech stack, is stored in PostgreSQL
- [x] A Repository Setup never counts towards the maximum Active Intents (a test states this, so the Intent Lifecycle can't count it later by accident)
- [x] The real Gateway's create-repository call is covered by the opt-in GitHub test
- [x] Covered by tests through the application with the fake GitHub Gateway
