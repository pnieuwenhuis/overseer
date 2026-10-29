# Purges the Key Vault once deleting its resource group has soft-deleted it, unless it isn't soft-deleted. Until it
# is purged its name stays taken, and an install with the same prefix would fail to create it.
set -euo pipefail

vault={{KeyVault}}

if [ -n "$(az keyvault list-deleted --resource-type vault --query "[?name=='$vault'].name" --output tsv)" ]; then
  az keyvault purge --name "$vault" --output none
fi
