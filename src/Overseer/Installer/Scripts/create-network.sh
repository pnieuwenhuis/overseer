# Creates Overseer's VNet: one subnet for the Container Apps environment, one for PostgreSQL. 10.41.0.0/16 stays free
# for the Sandbox VNet that will be peered with it.
set -euo pipefail

resource_group={{ResourceGroup}}
vnet={{VirtualNetwork}}
location={{Location}}

az network vnet create \
  --resource-group "$resource_group" \
  --name "$vnet" \
  --location "$location" \
  --address-prefixes 10.40.0.0/16 \
  --output none

az network vnet subnet create \
  --resource-group "$resource_group" \
  --vnet-name "$vnet" \
  --name container-apps \
  --address-prefixes 10.40.0.0/23 \
  --delegations Microsoft.App/environments \
  --output none

az network vnet subnet create \
  --resource-group "$resource_group" \
  --vnet-name "$vnet" \
  --name postgres \
  --address-prefixes 10.40.2.0/24 \
  --delegations Microsoft.DBforPostgreSQL/flexibleServers \
  --output none
