# 06: Configured Repositories

**What to build:** On the Settings page the user registers GitHub repositories as Configured Repositories, by `owner/name` (a pasted GitHub URL works too). Before registering, Overseer checks through the GitHub Gateway that the stored token can reach the repository. The page lists the Configured Repositories, and the user can remove one. Covers user story 17.

The guard against removing a repository that an open Intent has in scope (story 18) comes with the Intent tickets, once Intents and Repositories in scope exist.

**Blocked by:** 05 (GitHub token and the GitHub Gateway)

**Status:** done

- [x] The user can add a Configured Repository as `owner/name` or as a GitHub URL; both end up stored as `owner/name`
- [x] Adding fails with a clear message when no GitHub token is set, when the repository doesn't exist, or when the token can't reach it
- [x] Adding a repository that is already configured is refused
- [x] The Settings page lists the Configured Repositories; they survive a restart
- [x] The user can remove a Configured Repository
- [x] Covered by tests through the application with the fake GitHub Gateway
