# Deletes every Azure role assignment of Overseer's Managed Identity, wherever it is scoped, unless the identity is
# gone. Deleting the identity, with its resource group, would leave them behind, orphaned. Doing it first also takes
# the running Overseer's rights on the Sandbox resource group before uninstall deletes it.
set -euo pipefail

resource_group={{ResourceGroup}}
identity_name={{Identity}}

principal_id=$(az identity list \
  --resource-group "$resource_group" \
  --query "[?name=='$identity_name'].principalId" \
  --output tsv)
if [ -z "$principal_id" ]; then
  exit 0
fi

# By principal ID, so nothing needs Microsoft Graph.
assignments=$(az role assignment list \
  --all \
  --fill-principal-name false \
  --query "[?principalId=='$principal_id'].id" \
  --output tsv)
if [ -n "$assignments" ]; then
  # Unquoted on purpose: one --ids value per line. IDs have no spaces.
  az role assignment delete --ids $assignments --output none
fi
