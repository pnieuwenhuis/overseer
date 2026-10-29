# Prints how to reach a Sandbox, one per line: its VM's IP address, public or, on the private network, private, and the
# fingerprint of the SSH host key it was given, from the VM's tag.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
vm={{VirtualMachine}}
private_network={{PrivateNetwork}}

if [ "$private_network" = true ]; then
  address=privateIps
else
  address=publicIps
fi

az vm show \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$vm" \
  --show-details \
  --query "[$address, tags.\"overseer-host-key\"]" \
  --output tsv
