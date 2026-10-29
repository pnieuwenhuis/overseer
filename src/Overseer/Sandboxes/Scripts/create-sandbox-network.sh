# Creates the VNet that Sandboxes share, sandboxes, with one subnet of the same name, unless it exists. It uses
# 10.41.0.0/16, the range the installer leaves free next to Overseer's own VNet.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}

if [ -z "$(az network vnet list --subscription "$subscription_id" --resource-group "$resource_group" --query "[?name=='sandboxes'].name" --output tsv)" ]; then
  az network vnet create \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name sandboxes \
    --address-prefixes 10.41.0.0/16 \
    --subnet-name sandboxes \
    --subnet-prefixes 10.41.0.0/16 \
    --output none
fi
