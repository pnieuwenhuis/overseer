# Creates the Overseer resource group, for Overseer itself, and the Sandbox resource group, for its Sandboxes.
# Both are tagged with the prefix that identifies the installation.
set -euo pipefail

resource_group={{ResourceGroup}}
sandbox_resource_group={{SandboxResourceGroup}}
location={{Location}}
prefix={{Prefix}}

for group in "$resource_group" "$sandbox_resource_group"; do
  az group create --name "$group" --location "$location" --tags "overseer-prefix=$prefix" --output none
done
