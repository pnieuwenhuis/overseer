# Checks that the Azure CLI is installed and logged in, and prints the subscription it will use:
# its ID, its name and the signed-in user, one per line.
set -euo pipefail

if ! command -v az >/dev/null; then
  echo "The Azure CLI (az) is not installed, or not on the PATH. Install it from https://learn.microsoft.com/cli/azure/install-azure-cli, run \`az login\`, and run the command again." >&2
  exit 1
fi

if ! az account show --query '[id, name, user.name]' --output tsv; then
  echo "The Azure CLI is not logged in. Run \`az login\`, select the subscription Overseer is installed in (or to install it into), and run the command again." >&2
  exit 1
fi
