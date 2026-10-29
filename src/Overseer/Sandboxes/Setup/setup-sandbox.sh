#!/bin/bash
# Installs what every Sandbox has, as root, from its cloud-init: Docker with Compose, git, gh, Node with Playwright and
# its browsers, and OpenCode. A failing step fails cloud-init, which fails the creation of the Sandbox.
set -euxo pipefail
export DEBIAN_FRONTEND=noninteractive

# gh and a current Node come from their own repositories: Ubuntu's are too old.
install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli-archive-keyring.gpg
chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
  > /etc/apt/sources.list.d/github-cli.list
curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg
echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_24.x nodistro main" \
  > /etc/apt/sources.list.d/nodesource.list

apt-get update
apt-get install -y docker.io docker-compose-v2 git gh nodejs
# The user Overseer logs in as (AzureSandboxProvider.Username) runs Docker without sudo.
usermod -aG docker overseer

# The browsers go where every user finds them, rather than in root's home.
echo 'PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright' >> /etc/environment
export PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright
npm install -g playwright opencode-ai
playwright install --with-deps

gh_version=$(gh --version | head -n 1 | cut -d ' ' -f 3)
if ! dpkg --compare-versions "$gh_version" ge 2.99; then
  echo "gh $gh_version is installed, but Sandboxes need 2.99 or newer." >&2
  exit 1
fi
docker --version
docker compose version
git --version
node --version
opencode --version
