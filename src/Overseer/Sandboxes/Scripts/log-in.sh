# Logs the Azure CLI in as Overseer's user-assigned Managed Identity, for the scripts that follow. That is how the
# deployed Overseer reaches the Sandbox resource group, the only one where the identity has a role.
set -euo pipefail

client_id={{ClientId}}

az login --identity --client-id "$client_id" --output none
