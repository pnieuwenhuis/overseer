# Prints how to reach a Sandbox, one per line: its VM's public IP address, and the fingerprint of the SSH host key it
# was given, from the VM's tag.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
vm={{VirtualMachine}}

az vm show \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$vm" \
  --show-details \
  --query "[publicIps, tags.\"overseer-host-key\"]" \
  --output tsv
