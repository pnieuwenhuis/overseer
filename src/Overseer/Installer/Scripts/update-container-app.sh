# Updates the existing Container App to the given image and to the settings create-container-app.sh gives a new one.
# Its secrets stay as they are, so the password hash, and with it the password, is kept. Deploying the image it
# already runs with the same settings creates no new revision. Prints the Portal's host name.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
sandbox_resource_group={{SandboxResourceGroup}}
container_app={{ContainerApp}}
image={{Image}}
identity_client_id={{IdentityClientId}}
connection_string={{ConnectionString}}
vault_uri={{KeyVaultUri}}
username={{Username}}

az containerapp update \
  --resource-group "$resource_group" \
  --name "$container_app" \
  --image "$image" \
  --min-replicas 1 \
  --max-replicas 1 \
  --cpu 1.0 \
  --memory 2.0Gi \
  --replace-env-vars \
    "OVERSEER_CONNECTIONSTRINGS__OVERSEER=$connection_string" \
    OVERSEER_DATABASE__ENTRAAUTHENTICATION=true \
    "OVERSEER_DATABASE__MANAGEDIDENTITYCLIENTID=$identity_client_id" \
    "OVERSEER_SECRETS__KEYVAULTURI=$vault_uri" \
    "OVERSEER_SECRETS__MANAGEDIDENTITYCLIENTID=$identity_client_id" \
    "OVERSEER_SANDBOXPROVIDER__AZURE__SUBSCRIPTIONID=$subscription_id" \
    "OVERSEER_SANDBOXPROVIDER__AZURE__RESOURCEGROUP=$sandbox_resource_group" \
    OVERSEER_SANDBOXPROVIDER__AZURE__PRIVATENETWORK=true \
    "OVERSEER_SANDBOXPROVIDER__AZURE__MANAGEDIDENTITYCLIENTID=$identity_client_id" \
    "OVERSEER_USER__NAME=$username" \
    OVERSEER_USER__PASSWORDHASH=secretref:password-hash \
  --query properties.configuration.ingress.fqdn \
  --output tsv
