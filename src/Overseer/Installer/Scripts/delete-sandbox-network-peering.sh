# Deletes the peering between Overseer's VNet and the Sandbox VNet, both ways, unless it or its VNet doesn't exist, so
# that neither resource group's deletion is held up by, or leaves behind, a peering with the other.
set -euo pipefail

resource_group={{ResourceGroup}}
vnet={{VirtualNetwork}}
sandbox_resource_group={{SandboxResourceGroup}}
sandbox_vnet={{SandboxVirtualNetwork}}

# Deletes the peering $3 of the VNet $2 in the resource group $1, unless the VNet or the peering doesn't exist.
unpeer() {
  if [ -n "$(az network vnet list --resource-group "$1" --query "[?name=='$2'].name" --output tsv)" ] \
    && [ -n "$(az network vnet peering list --resource-group "$1" --vnet-name "$2" --query "[?name=='$3'].name" --output tsv)" ]; then
    az network vnet peering delete --resource-group "$1" --vnet-name "$2" --name "$3" --output none
  fi
}

unpeer "$resource_group" "$vnet" "$sandbox_vnet"
unpeer "$sandbox_resource_group" "$sandbox_vnet" "$vnet"
