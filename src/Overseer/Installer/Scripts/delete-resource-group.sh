# Deletes the resource group and everything in it, and waits until it is gone, unless it doesn't exist. It looks at
# the group's overseer-prefix tag again first: a group without it isn't Overseer's and is never deleted.
set -euo pipefail

resource_group={{ResourceGroup}}
prefix={{Prefix}}

if [ "$(az group exists --name "$resource_group")" != true ]; then
  exit 0
fi
if [ "$(az group show --name "$resource_group" --query "tags.\"overseer-prefix\" || '-'" --output tsv)" != "$prefix" ]; then
  echo "The resource group $resource_group lacks the tag overseer-prefix=$prefix, so Overseer didn't create it. Uninstall leaves it alone." >&2
  exit 1
fi

az group delete --name "$resource_group" --yes --output none
