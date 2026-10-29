# Finds the installation with this prefix by its Overseer resource group. Prints nothing when there is none. Otherwise
# prints the group's location, its overseer-prefix tag and the image the Container App runs, one per line, with - for
# a missing tag or a missing Container App (an install that stopped before creating it). A group without the
# prefix's tag isn't Overseer's, so its Container Apps aren't looked at.
set -euo pipefail

resource_group={{ResourceGroup}}
container_app={{ContainerApp}}
prefix={{Prefix}}

group=$(az group list \
  --query "[?name=='$resource_group'].[location, tags.\"overseer-prefix\" || '-'] | []" \
  --output tsv)
if [ -z "$group" ]; then
  exit 0
fi

image=-
if [ "$(sed -n 2p <<<"$group")" = "$prefix" ]; then
  image=$(az containerapp list \
    --resource-group "$resource_group" \
    --query "[?name=='$container_app'].properties.template.containers[0].image | [0] || '-'" \
    --output tsv)
fi

printf '%s\n%s\n' "$group" "$image"
