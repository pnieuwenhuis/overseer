# Sets the Container App's password-hash secret and prints the username the password goes with. A secret belongs to
# the app, not to a revision, so this creates no revision: the running one reads the new hash once it restarts.
set -euo pipefail

resource_group={{ResourceGroup}}
container_app={{ContainerApp}}
password_hash={{PasswordHash}}

username=$(az containerapp show \
  --resource-group "$resource_group" \
  --name "$container_app" \
  --query "properties.template.containers[0].env[?name=='OVERSEER_USER__NAME'].value | [0]" \
  --output tsv)
if [ -z "$username" ]; then
  echo "The Container App $container_app has no OVERSEER_USER__NAME setting, so it has no user to give a password. Run install again to restore its settings." >&2
  exit 1
fi

az containerapp secret set \
  --resource-group "$resource_group" \
  --name "$container_app" \
  --secrets "password-hash=$password_hash" \
  --output none

echo "$username"
