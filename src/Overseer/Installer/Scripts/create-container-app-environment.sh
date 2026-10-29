# Creates the Container Apps environment on Overseer's VNet, sending its logs to a Log Analytics workspace, unless it
# exists.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
vnet={{VirtualNetwork}}
environment={{ContainerAppsEnvironment}}
workspace={{LogAnalyticsWorkspace}}
location={{Location}}

if [ -n "$(az containerapp env list --resource-group "$resource_group" --query "[?name=='$environment'].name" --output tsv)" ]; then
  exit 0
fi

workspace_id=$(az monitor log-analytics workspace list \
  --resource-group "$resource_group" \
  --query "[?name=='$workspace'].customerId" \
  --output tsv)
if [ -z "$workspace_id" ]; then
  workspace_id=$(az monitor log-analytics workspace create \
    --resource-group "$resource_group" \
    --workspace-name "$workspace" \
    --location "$location" \
    --query customerId \
    --output tsv)
fi
workspace_key=$(az monitor log-analytics workspace get-shared-keys \
  --resource-group "$resource_group" \
  --workspace-name "$workspace" \
  --query primarySharedKey \
  --output tsv)

az containerapp env create \
  --resource-group "$resource_group" \
  --name "$environment" \
  --location "$location" \
  --infrastructure-subnet-resource-id "/subscriptions/$subscription_id/resourceGroups/$resource_group/providers/Microsoft.Network/virtualNetworks/$vnet/subnets/container-apps" \
  --logs-destination log-analytics \
  --logs-workspace-id "$workspace_id" \
  --logs-workspace-key "$workspace_key" \
  --output none
