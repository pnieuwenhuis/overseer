# 08: Model provider tokens

**What to build:** On the Settings page the user stores model provider tokens (for example for Azure Foundry or an AI gateway), each under a name. The tokens go into the write-only secret store and are never shown again. Each token is made available to agent sessions as an environment variable that the agent harness configuration (`opencode.json`) references. Switching provider means adding a token and editing the configuration, without any change to Overseer. Covers user story 25.

**Blocked by:** 07 (Agent harness configuration)

**Status:** done

- [x] The Settings page lists the stored provider tokens by name, never their values, and offers add, replace and remove
- [x] A token's name maps to a stated environment variable name, which the page shows so the user can reference it from `opencode.json`
- [x] Every agent session started receives all stored provider tokens as environment variables (verified through the fake Sandbox Provider); a removed token is no longer passed
- [x] Saving a harness configuration that references an environment variable no stored token provides is allowed, with a warning naming it
- [x] Tokens never reach PostgreSQL, the logs or any Portal component (the existing `PortalSecretsTests` and secret leak tests cover them)
- [x] Covered by tests through the application
