# 11: Azure Sandbox Provider: deployed mode, no public IP

**What to build:** When Overseer runs in Azure, its Sandboxes have no public IP and nothing in them is reachable from the internet. Overseer reaches them over private networking, and they still have outbound internet access for GitHub, model providers and package registries. Covers user story 11.

Constraint from story 8: the Managed Identity has rights only on the Sandbox resource group, so Sandboxes can't join a subnet in the Overseer resource group. The Sandbox VNet therefore lives in the Sandbox resource group, peered with the Overseer VNet. New VNets no longer get default outbound access, so outbound traffic goes through a NAT Gateway (a small fixed monthly cost).

**Blocked by:** 07 (Re-running `install` upgrades in place), 09 (`uninstall`), 10 (Azure Sandbox Provider: local mode)

**Status:** done

- [x] Install creates a Sandbox VNet in the Sandbox resource group, peered in both directions with the Overseer VNet, plus a NAT Gateway for outbound traffic; re-running install converges these too
- [x] The Managed Identity's only Azure RBAC role assignment is still Contributor on the Sandbox resource group
- [x] The deployed Overseer uses the Azure provider with its Managed Identity, without extra configuration
- [x] Sandboxes created by the deployed Overseer have no public IP and accept no inbound traffic from the internet; Overseer reaches them by private IP
- [x] Sandboxes can reach the internet outbound
- [x] Uninstall also removes the peering and the NAT Gateway
- [x] An opt-in end-to-end smoke test runs install, has the deployed Overseer create a Sandbox, runs a command in it, destroys it, and uninstalls; whatever triggers Sandbox creation for the test is not exposed in a normal installation

The image now comes from a `Dockerfile` (aspnet:10.0 with a pinned Azure CLI) instead of `PublishContainer`, because the deployed provider manages Sandboxes through the Azure CLI. The smoke-test trigger is `/smoke-test/sandboxes`, mapped only with `SmokeTest:Enabled`, which install never sets, and only for the logged-in user. `DeployedSandboxSmokeTest` has not yet been run against a real subscription: it needs a released version whose image has the Azure CLI. The image was built locally and passes `scripts/smoke-test-image.sh`, including its Azure CLI check.
