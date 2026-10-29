# Creates Overseer's VNet, with one subnet for the Container Apps environment and one for PostgreSQL, unless they
# exist. 10.41.0.0/16 stays free for the Sandbox VNet that will be peered with it.
set -euo pipefail

resource_group={{ResourceGroup}}
vnet={{VirtualNetwork}}
location={{Location}}

if [ -z "$(az network vnet list --resource-group "$resource_group" --query "[?name=='$vnet'].name" --output tsv)" ]; then
  az network vnet create \
    --resource-group "$resource_group" \
    --name "$vnet" \
    --location "$location" \
    --address-prefixes 10.40.0.0/16 \
    --output none
fi

# Creates the subnet $1 with the address prefix $2, delegated to $3, unless it exists.
subnet() {
  if [ -z "$(az network vnet subnet list --resource-group "$resource_group" --vnet-name "$vnet" --query "[?name=='$1'].name" --output tsv)" ]; then
    az network vnet subnet create \
      --resource-group "$resource_group" \
      --vnet-name "$vnet" \
      --name "$1" \
      --address-prefixes "$2" \
      --delegations "$3" \
      --output none
  fi
}

subnet container-apps 10.40.0.0/23 Microsoft.App/environments
subnet postgres 10.40.2.0/24 Microsoft.DBforPostgreSQL/flexibleServers
