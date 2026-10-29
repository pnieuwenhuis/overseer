# Creates the network Sandboxes use in the Sandbox resource group, unless it exists, and makes an existing one complete:
# the VNet the Azure Sandbox Provider puts Sandboxes on, in 10.41.0.0/16 next to Overseer's 10.40.0.0/16, and the
# peering, both ways, through which Overseer reaches them by private IP. Sandboxes have no public IP, and new VNets no
# longer get default outbound access, so their subnet reaches the internet through a NAT Gateway. It is in the Sandbox
# resource group because the Managed Identity, which creates the Sandboxes, has rights there only.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
vnet={{VirtualNetwork}}
sandbox_resource_group={{SandboxResourceGroup}}
sandbox_vnet={{SandboxVirtualNetwork}}
nat={{NatGateway}}
location={{Location}}

if [ -z "$(az network public-ip list --resource-group "$sandbox_resource_group" --query "[?name=='$nat-ip'].name" --output tsv)" ]; then
  az network public-ip create \
    --resource-group "$sandbox_resource_group" \
    --name "$nat-ip" \
    --location "$location" \
    --sku Standard \
    --allocation-method Static \
    --output none
fi
if [ -z "$(az network nat gateway list --resource-group "$sandbox_resource_group" --query "[?name=='$nat'].name" --output tsv)" ]; then
  az network nat gateway create \
    --resource-group "$sandbox_resource_group" \
    --name "$nat" \
    --location "$location" \
    --public-ip-addresses "$nat-ip" \
    --output none
fi

if [ -z "$(az network vnet list --resource-group "$sandbox_resource_group" --query "[?name=='$sandbox_vnet'].name" --output tsv)" ]; then
  az network vnet create \
    --resource-group "$sandbox_resource_group" \
    --name "$sandbox_vnet" \
    --location "$location" \
    --address-prefixes 10.41.0.0/16 \
    --output none
fi
# The subnet has the VNet's name. One the Azure Sandbox Provider created in local mode has no NAT Gateway yet.
if [ -z "$(az network vnet subnet list --resource-group "$sandbox_resource_group" --vnet-name "$sandbox_vnet" --query "[?name=='$sandbox_vnet'].name" --output tsv)" ]; then
  az network vnet subnet create \
    --resource-group "$sandbox_resource_group" \
    --vnet-name "$sandbox_vnet" \
    --name "$sandbox_vnet" \
    --address-prefixes 10.41.0.0/16 \
    --nat-gateway "$nat" \
    --default-outbound-access false \
    --output none
else
  subnet_nat=$(az network vnet subnet show \
    --resource-group "$sandbox_resource_group" \
    --vnet-name "$sandbox_vnet" \
    --name "$sandbox_vnet" \
    --query natGateway.id \
    --output tsv)
  if [ "${subnet_nat##*/}" != "$nat" ]; then
    az network vnet subnet update \
      --resource-group "$sandbox_resource_group" \
      --vnet-name "$sandbox_vnet" \
      --name "$sandbox_vnet" \
      --nat-gateway "$nat" \
      --output none
  fi
fi

# Peers the VNet $2 in the resource group $1 with the VNet whose ID is $4, as the peering $3, unless that exists. A
# peering stays Initiated until the other way exists too. One whose other VNet was deleted and created again is
# Disconnected for good, so it is created again.
peer() {
  state=$(az network vnet peering list \
    --resource-group "$1" \
    --vnet-name "$2" \
    --query "[?name=='$3'].peeringState | [0]" \
    --output tsv)
  if [ "$state" = Disconnected ]; then
    az network vnet peering delete --resource-group "$1" --vnet-name "$2" --name "$3" --output none
  fi
  if [ -z "$state" ] || [ "$state" = Disconnected ]; then
    az network vnet peering create --resource-group "$1" --vnet-name "$2" --name "$3" --remote-vnet "$4" --allow-vnet-access true --output none
  fi
}

networks="/subscriptions/$subscription_id/resourceGroups"
peer "$resource_group" "$vnet" "$sandbox_vnet" "$networks/$sandbox_resource_group/providers/Microsoft.Network/virtualNetworks/$sandbox_vnet"
peer "$sandbox_resource_group" "$sandbox_vnet" "$vnet" "$networks/$resource_group/providers/Microsoft.Network/virtualNetworks/$vnet"
