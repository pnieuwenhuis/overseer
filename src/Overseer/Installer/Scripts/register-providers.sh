# Registers the Azure resource providers for everything install creates. A subscription that never used one of them
# has it unregistered, and creating its resources would fail. Registering again changes nothing.
set -euo pipefail

for namespace in Microsoft.App Microsoft.DBforPostgreSQL Microsoft.KeyVault Microsoft.ManagedIdentity Microsoft.Network Microsoft.OperationalInsights; do
  az provider register --namespace "$namespace" --wait --output none
done
