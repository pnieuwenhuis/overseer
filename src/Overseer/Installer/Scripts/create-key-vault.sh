# Creates the Key Vault for Overseer's secrets, unless it exists: creating it again would replace its access policies.
# It uses access policies instead of Azure RBAC, so the Managed Identity's only role assignment stays Contributor on
# the Sandbox resource group. Only the Managed Identity gets a policy, for secrets only: Overseer sets, reads, removes
# and recovers them (reading a removed one takes list). Prints the vault's URI.
set -euo pipefail

resource_group={{ResourceGroup}}
vault={{KeyVault}}
location={{Location}}
identity_principal_id={{IdentityPrincipalId}}

if [ -n "$(az keyvault list --resource-group "$resource_group" --query "[?name=='$vault'].name" --output tsv)" ]; then
  vault_uri=$(az keyvault show \
    --resource-group "$resource_group" \
    --name "$vault" \
    --query properties.vaultUri \
    --output tsv)
else
  vault_uri=$(az keyvault create \
    --resource-group "$resource_group" \
    --name "$vault" \
    --location "$location" \
    --enable-rbac-authorization false \
    --no-self-perms \
    --query properties.vaultUri \
    --output tsv)
fi

# Setting the same policy again changes nothing.
az keyvault set-policy \
  --resource-group "$resource_group" \
  --name "$vault" \
  --object-id "$identity_principal_id" \
  --secret-permissions get list set delete recover \
  --output none

printf '%s\n' "$vault_uri"
