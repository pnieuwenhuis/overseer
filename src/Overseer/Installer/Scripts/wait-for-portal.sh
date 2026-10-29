# Waits until the replica of the Container App's latest revision is ready: Overseer has migrated its database and
# listens. After an upgrade, the revision it replaces keeps serving until then, so only the latest one counts. Gives
# up after 10 minutes and says where to find out why.
set -euo pipefail

resource_group={{ResourceGroup}}
container_app={{ContainerApp}}

revision=$(az containerapp show \
  --resource-group "$resource_group" \
  --name "$container_app" \
  --query properties.latestRevisionName \
  --output tsv)

for _ in $(seq 60); do
  ready=$(az containerapp replica list \
    --resource-group "$resource_group" \
    --name "$container_app" \
    --revision "$revision" \
    --query '[0].properties.containers[0].ready' \
    --output tsv) || ready=  # A transient Azure error is just another try.
  if [ "$ready" = true ]; then
    exit 0
  fi
  sleep 10
done

echo "The Portal did not become ready within 10 minutes. See why in its logs: az containerapp logs show --resource-group $resource_group --name $container_app --tail 100" >&2
exit 1
