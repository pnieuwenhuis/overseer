# Prints the VMs in the Sandbox resource group that are tagged with an owner, one per line: the VM's name, its owner's
# kind and its owner's ID, separated by tabs.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}

az vm list \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --query "[?tags.\"overseer-owner-kind\" && tags.\"overseer-owner-id\"].[name, tags.\"overseer-owner-kind\", tags.\"overseer-owner-id\"]" \
  --output tsv
