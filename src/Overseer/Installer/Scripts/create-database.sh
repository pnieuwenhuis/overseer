# Creates the PostgreSQL server, private on the VNet, with Microsoft Entra authentication only, and its database,
# unless they exist. The Managed Identity is the server's administrator and only login, so no database password
# exists. Prints the server's host name.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
vnet={{VirtualNetwork}}
server={{DatabaseServer}}
database={{Database}}
location={{Location}}
identity_name={{Identity}}
identity_principal_id={{IdentityPrincipalId}}

if [ -z "$(az postgres flexible-server list --resource-group "$resource_group" --query "[?name=='$server'].name" --output tsv)" ]; then
  az postgres flexible-server create \
    --resource-group "$resource_group" \
    --name "$server" \
    --location "$location" \
    --tier Burstable \
    --sku-name Standard_B1ms \
    --storage-size 32 \
    --version 17 \
    --subnet "/subscriptions/$subscription_id/resourceGroups/$resource_group/providers/Microsoft.Network/virtualNetworks/$vnet/subnets/postgres" \
    --private-dns-zone "$server.private.postgres.database.azure.com" \
    --microsoft-entra-auth Enabled \
    --password-auth Disabled \
    --admin-object-id "$identity_principal_id" \
    --admin-display-name "$identity_name" \
    --admin-type ServicePrincipal \
    --yes \
    --output none
fi

if [ -z "$(az postgres flexible-server db list --resource-group "$resource_group" --server-name "$server" --query "[?name=='$database'].name" --output tsv)" ]; then
  az postgres flexible-server db create \
    --resource-group "$resource_group" \
    --server-name "$server" \
    --database-name "$database" \
    --output none
fi

az postgres flexible-server show \
  --resource-group "$resource_group" \
  --name "$server" \
  --query fullyQualifiedDomainName \
  --output tsv
