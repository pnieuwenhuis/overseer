# Creates Overseer's user-assigned Managed Identity and gives it its only Azure RBAC role, Contributor on the Sandbox
# resource group, unless they exist. Prints the identity's resource ID, principal ID and client ID, one per line.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
sandbox_resource_group={{SandboxResourceGroup}}
identity_name={{Identity}}
location={{Location}}

identity=$(az identity list \
  --resource-group "$resource_group" \
  --query "[?name=='$identity_name'].[id, principalId, clientId] | []" \
  --output tsv)
if [ -z "$identity" ]; then
  identity=$(az identity create \
    --resource-group "$resource_group" \
    --name "$identity_name" \
    --location "$location" \
    --query '[id, principalId, clientId]' \
    --output tsv)
fi
principal_id=$(sed -n 2p <<<"$identity")

scope="/subscriptions/$subscription_id/resourceGroups/$sandbox_resource_group"
assignment=$(az role assignment list \
  --scope "$scope" \
  --role Contributor \
  --fill-principal-name false \
  --query "[?principalId=='$principal_id'].id" \
  --output tsv)
if [ -z "$assignment" ]; then
  # By object ID and principal type, so a just-created identity needn't have reached Microsoft Graph yet.
  az role assignment create \
    --assignee-object-id "$principal_id" \
    --assignee-principal-type ServicePrincipal \
    --role Contributor \
    --scope "$scope" \
    --output none
fi

printf '%s\n' "$identity"
