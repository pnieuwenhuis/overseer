# Creates the Overseer resource group, for Overseer itself, and the Sandbox resource group, for its Sandboxes, unless
# they exist. Both are tagged with the prefix that identifies the installation; an existing group without that tag
# isn't Overseer's, so install stops instead of taking it over.
set -euo pipefail

resource_group={{ResourceGroup}}
sandbox_resource_group={{SandboxResourceGroup}}
location={{Location}}
prefix={{Prefix}}

for group in "$resource_group" "$sandbox_resource_group"; do
  if [ "$(az group exists --name "$group")" != true ]; then
    az group create --name "$group" --location "$location" --tags "overseer-prefix=$prefix" --output none
  elif [ "$(az group show --name "$group" --query "tags.\"overseer-prefix\" || '-'" --output tsv)" != "$prefix" ]; then
    echo "The resource group $group exists, but Overseer didn't create it: it lacks the tag overseer-prefix=$prefix. Choose another --prefix." >&2
    exit 1
  fi
done
