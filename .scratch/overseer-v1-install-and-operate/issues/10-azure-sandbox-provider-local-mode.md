# 10: Azure Sandbox Provider: local mode

**What to build:** A developer running Overseer locally can create and use real Sandboxes in Azure that only their own IP can reach, so they can test end-to-end from their machine. This is the Azure implementation of the Sandbox Provider port from 04, in its local-mode networking. Like the installer, it manages Azure only through the Azure CLI. Covers user story 13.

Out of scope here: cloning the Repositories in scope, running manifest setup, starting Compose dependencies (stories 43 and 63), and the real ACP agent session (the Agent Session module). Contract tests for agent sessions are skipped for this provider until that lands.

**Blocked by:** 04 (Fake Sandbox Provider)

**Status:** ready-for-agent

- [ ] The Azure provider is selected through configuration (subscription and Sandbox resource group) and uses the developer's Azure login
- [ ] Create makes one VM per Sandbox in the Sandbox resource group, tagged with its owner ID, at a configurable VM size with a sensible default
- [ ] The base cloud-init installs Docker, git, `gh` 2.99 or newer, OpenCode, and Node with Playwright and browsers; create only reports success once cloud-init has finished
- [ ] Each Sandbox gets a fresh SSH key pair; the private key is kept in the secret store, not in PostgreSQL, so it survives a restart
- [ ] The Sandbox has a public IP, with SSH allowed only from the developer's current public IP
- [ ] Run-command executes over SSH and returns the exit code and output
- [ ] Destroy removes the VM and everything created with it (disk, NIC, public IP, network security group)
- [ ] List returns the Sandboxes in the Sandbox resource group with their owner tags
- [ ] A creation failure surfaces the Azure error message
- [ ] The provider contract suite passes against a real subscription as an opt-in test
