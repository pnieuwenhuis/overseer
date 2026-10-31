# 05: GitHub token and the GitHub Gateway

**What to build:** The user stores their GitHub fine-grained token on the Settings page, and can replace or remove it. The token goes into the write-only secret store and is never shown again. On save, Overseer checks the token through the GitHub Gateway and shows the result: "set, works as `<login>`" or GitHub's error. This ticket introduces the GitHub Gateway, the only component that calls the GitHub API, with a fake implementation for tests (seam 2 in the spec's Testing Decisions). Its first operation is checking the token. Covers user story 19.

**Blocked by:** 02 (Settings page with operating limits)

**Status:** done

- [x] The Settings page shows whether a GitHub token is set, never its value, and offers set, replace and remove
- [x] Saving a token checks it through the GitHub Gateway; the page shows the GitHub login it works as, or GitHub's error message
- [x] A token that fails the check is still stored only if the user confirms it; otherwise the previous token stays in place
- [x] The GitHub Gateway reads the token itself through the secret reader; no Portal component reads a secret (the existing `PortalSecretsTests` still pass)
- [x] The token never reaches PostgreSQL or the logs (covered by the existing secret leak tests or an extension of them)
- [x] A fake GitHub Gateway is used in tests and can be scripted to accept or reject a token
- [x] The real Gateway is covered by an opt-in test against GitHub with a real token, not part of the default test run
