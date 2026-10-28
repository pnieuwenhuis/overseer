# Waits until the Container App's replica is ready: Overseer has migrated its database and listens. Gives up after
# 10 minutes and says where to find out why.
set -euo pipefail

resource_group={{ResourceGroup}}
container_app={{ContainerApp}}

for _ in $(seq 60); do
  ready=$(az containerapp replica list \
    --resource-group "$resource_group" \
    --name "$container_app" \
    --query '[0].properties.containers[0].ready' \
    --output tsv) || ready=  # A transient Azure error is just another try.
  if [ "$ready" = true ]; then
    exit 0
  fi
  sleep 10
done

echo "The Portal did not become ready within 10 minutes. See why in its logs: az containerapp logs show --resource-group $resource_group --name $container_app --tail 100" >&2
exit 1
