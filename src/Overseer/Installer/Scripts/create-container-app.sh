# Creates the Container App running Overseer: one replica, as its Managed Identity, which is how it logs in to
# PostgreSQL and reaches Key Vault. The password hash is a Container App secret. Prints the Portal's host name.
# update-container-app.sh gives an existing one the same settings.
set -euo pipefail

resource_group={{ResourceGroup}}
environment={{ContainerAppsEnvironment}}
container_app={{ContainerApp}}
image={{Image}}
identity_id={{IdentityId}}
identity_client_id={{IdentityClientId}}
connection_string={{ConnectionString}}
vault_uri={{KeyVaultUri}}
username={{Username}}
password_hash={{PasswordHash}}

az containerapp create \
  --resource-group "$resource_group" \
  --name "$container_app" \
  --environment "$environment" \
  --image "$image" \
  --user-assigned "$identity_id" \
  --min-replicas 1 \
  --max-replicas 1 \
  --cpu 0.5 \
  --memory 1.0Gi \
  --ingress external \
  --target-port 8080 \
  --secrets "password-hash=$password_hash" \
  --env-vars \
    "OVERSEER_CONNECTIONSTRINGS__OVERSEER=$connection_string" \
    OVERSEER_DATABASE__ENTRAAUTHENTICATION=true \
    "OVERSEER_DATABASE__MANAGEDIDENTITYCLIENTID=$identity_client_id" \
    "OVERSEER_SECRETS__KEYVAULTURI=$vault_uri" \
    "OVERSEER_SECRETS__MANAGEDIDENTITYCLIENTID=$identity_client_id" \
    "OVERSEER_USER__NAME=$username" \
    OVERSEER_USER__PASSWORDHASH=secretref:password-hash \
  --query properties.configuration.ingress.fqdn \
  --output tsv
