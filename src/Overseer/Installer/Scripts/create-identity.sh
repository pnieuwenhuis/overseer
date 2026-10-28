# Creates Overseer's user-assigned Managed Identity and gives it its only Azure RBAC role: Contributor on the
# Sandbox resource group. Prints the identity's resource ID, principal ID and client ID, one per line.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
sandbox_resource_group={{SandboxResourceGroup}}
identity_name={{Identity}}
location={{Location}}

identity=$(az identity create \
  --resource-group "$resource_group" \
  --name "$identity_name" \
  --location "$location" \
  --query '[id, principalId, clientId]' \
  --output tsv)
principal_id=$(sed -n 2p <<<"$identity")

# By object ID and principal type, so a just-created identity needn't have reached Microsoft Graph yet.
az role assignment create \
  --assignee-object-id "$principal_id" \
  --assignee-principal-type ServicePrincipal \
  --role Contributor \
  --scope "/subscriptions/$subscription_id/resourceGroups/$sandbox_resource_group" \
  --output none

printf '%s\n' "$identity"
