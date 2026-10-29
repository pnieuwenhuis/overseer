# Restarts the Container App's active revision, so that Overseer reads its secrets again, and waits until a replica
# started by the restart is ready and the ones it replaces, which still have the old secrets, are gone. Gives up after
# 10 minutes and says where to find out why.
set -euo pipefail

resource_group={{ResourceGroup}}
container_app={{ContainerApp}}

# While an upgrade is under way the revision it replaces is active too; the newest one is the one that stays.
revision=$(az containerapp revision list \
  --resource-group "$resource_group" \
  --name "$container_app" \
  --query 'sort_by([?properties.active], &properties.createdTime)[-1].name' \
  --output tsv)
if [ -z "$revision" ]; then
  echo "The Container App $container_app has no active revision to restart. Run install again to deploy one." >&2
  exit 1
fi

# Each replica as its name and whether it is ready, tab-separated.
replicas() {
  az containerapp replica list \
    --resource-group "$resource_group" \
    --name "$container_app" \
    --revision "$revision" \
    --query '[].[name, properties.containers[0].ready]' \
    --output tsv
}

old_replicas=$(replicas | cut -f1)

az containerapp revision restart \
  --resource-group "$resource_group" \
  --name "$container_app" \
  --revision "$revision" \
  --output none

for _ in $(seq 60); do
  current=$(replicas) || current=  # A transient Azure error is just another try.
  old_gone=true
  new_ready=false
  while IFS=$'\t' read -r name ready; do
    if [ -z "$name" ]; then
      continue
    elif grep -qxF "$name" <<<"$old_replicas"; then
      old_gone=false
    elif [ "$ready" = true ]; then
      new_ready=true
    fi
  done <<<"$current"
  if $old_gone && $new_ready; then
    exit 0
  fi
  sleep 10
done

echo "The Container App did not finish restarting within 10 minutes. See why in its logs: az containerapp logs show --resource-group $resource_group --name $container_app --tail 100" >&2
exit 1
