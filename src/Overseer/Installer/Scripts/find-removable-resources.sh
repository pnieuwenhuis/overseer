# Finds what uninstall removes for this prefix. Prints, one per line, what the Overseer resource group and the
# Sandbox resource group are: overseer when it is tagged with the prefix, other when it exists without that tag (so
# Overseer didn't create it), absent when it doesn't exist. Then whether the Key Vault is soft-deleted: deleted or
# absent. A soft-deleted vault keeps its name taken until it is purged.
set -euo pipefail

resource_group={{ResourceGroup}}
sandbox_resource_group={{SandboxResourceGroup}}
vault={{KeyVault}}
prefix={{Prefix}}

for group in "$resource_group" "$sandbox_resource_group"; do
  if [ "$(az group exists --name "$group")" != true ]; then
    echo absent
  elif [ "$(az group show --name "$group" --query "tags.\"overseer-prefix\" || '-'" --output tsv)" = "$prefix" ]; then
    echo overseer
  else
    echo other
  fi
done

if [ -n "$(az keyvault list-deleted --resource-type vault --query "[?name=='$vault'].name" --output tsv)" ]; then
  echo deleted
else
  echo absent
fi
