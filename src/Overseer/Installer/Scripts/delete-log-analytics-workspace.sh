# Deletes the Log Analytics workspace that holds the Container Apps environment's logs for good, unless it doesn't
# exist. Deleting its resource group would only soft-delete it, and an install with the same prefix within 14 days
# would recover it, with the old installation's logs.
set -euo pipefail

resource_group={{ResourceGroup}}
workspace={{LogAnalyticsWorkspace}}

if [ -n "$(az monitor log-analytics workspace list --resource-group "$resource_group" --query "[?name=='$workspace'].name" --output tsv)" ]; then
  az monitor log-analytics workspace delete \
    --resource-group "$resource_group" \
    --workspace-name "$workspace" \
    --force true \
    --yes \
    --output none
fi
