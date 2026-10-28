# 05: Secret store

**What to build:** Overseer stores secrets (the GitHub token, model provider tokens) write-only. Once entered, a secret can be replaced, removed or checked for presence, but its value is never shown again. In Azure the secrets live in Key Vault, accessed through the Managed Identity. Locally they live in Key Vault through the developer's Azure login, or in a local secrets file. Covers user story 9; the screens for entering secrets come with Settings (stories 19 and 25).

**Blocked by:** 01 (Walking skeleton)

**Status:** ready-for-agent

- [ ] Secrets can be set, replaced, removed and checked for presence
- [ ] Only server-side consumers (for example, injecting a token into a Sandbox) can read a value; no Portal or API path returns one
- [ ] Secret values never reach PostgreSQL or the logs (covered by tests)
- [ ] The backend is chosen through layered configuration: Key Vault (Managed Identity, falling back to the developer's Azure login) or a local secrets file
- [ ] The local secrets file lives outside the repository by default, and its location is documented
- [ ] The file backend is covered by the default test run; the Key Vault backend by an opt-in test against a real vault
