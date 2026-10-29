# Registers the Azure resource providers for everything install creates, and Microsoft.Compute for the Sandboxes,
# whose VMs the Managed Identity creates without the right to register it. A subscription that never used one of them
# has it unregistered, and creating its resources would fail. Registering again changes nothing.
set -euo pipefail

for namespace in Microsoft.App Microsoft.Compute Microsoft.DBforPostgreSQL Microsoft.KeyVault Microsoft.ManagedIdentity Microsoft.Network Microsoft.OperationalInsights; do
  az provider register --namespace "$namespace" --wait --output none
done
