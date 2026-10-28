# 06: `install`: fresh installation

**What to build:** A user who is logged in with the Azure CLI runs `overseer install` and gets a working Overseer in their current subscription, plus the details to log in straight away. Covers user stories 1, 2, 3, 8 and 10, and the deploy side of 5.

Decided: the CLI generates the password and hashes it locally (same code as the server, see 03), then sets only the hash as a Container App secret exposed as an env var. This is a management-plane call, so the CLI never needs network access to the private database or VNet.

**Blocked by:** 02 (Versioned release), 03 (Single-user credential), 05 (Secret store)

**Status:** ready-for-agent

- [ ] Azure is managed only through the Azure CLI, using templated bash scripts embedded in the executable
- [ ] Before creating anything, install checks that the Azure CLI is installed and logged in, and shows which subscription it will use
- [ ] `--region` (default westeurope) and `--prefix` set the location and the resource names
- [ ] Install creates the Overseer resource group: a Container Apps environment on a VNet; the Container App (single replica, min = max = 1) running the image tag equal to the CLI's version; PostgreSQL Flexible Server (Burstable tier) with Entra authentication only; Key Vault; a user-assigned Managed Identity
- [ ] Install creates the Sandbox resource group; the Managed Identity's only Azure RBAC role assignment is Contributor on that group, and its data-plane access is limited to its own Key Vault's secrets and its own PostgreSQL login
- [ ] The application connects to PostgreSQL with the Managed Identity; no database password exists anywhere
- [ ] The deployed application uses the Key Vault secret store backend
- [ ] Install generates a password, sets its hash as the Container App secret, and prints the Portal URL, the username and the password; logging in with them works
- [ ] Running install from a development build, which has no published image, fails with a clear message
- [ ] A failing step (quota, unsupported region, missing permissions) stops with an actionable message that includes the Azure error
- [ ] An opt-in smoke test installs into a real subscription and logs in
