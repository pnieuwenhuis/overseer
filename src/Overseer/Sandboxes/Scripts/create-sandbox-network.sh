# Creates the VNet that Sandboxes share, sandboxes, with one subnet of the same name, unless it exists. It uses
# 10.41.0.0/16, the range the installer leaves free next to Overseer's own VNet. On the private network, Sandboxes need
# what install creates with that VNet instead: the NAT Gateway they reach the internet through and the peering through
# which Overseer reaches them. So then this only checks that the VNet exists.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
private_network={{PrivateNetwork}}

if [ -z "$(az network vnet list --subscription "$subscription_id" --resource-group "$resource_group" --query "[?name=='sandboxes'].name" --output tsv)" ]; then
  if [ "$private_network" = true ]; then
    echo "The Sandbox resource group $resource_group has no VNet sandboxes, which install creates with the NAT Gateway through which Sandboxes reach the internet and the peering through which Overseer reaches them. Run \`overseer install\` again to create it." >&2
    exit 1
  fi
  az network vnet create \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name sandboxes \
    --address-prefixes 10.41.0.0/16 \
    --subnet-name sandboxes \
    --subnet-prefixes 10.41.0.0/16 \
    --output none
fi
