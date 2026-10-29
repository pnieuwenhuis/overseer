# Creates a Sandbox's VM and everything it needs, each named after the VM so that destroy-sandbox.sh finds them: a
# network security group that lets in SSH only from Overseer's address (the developer's public IP, or a deployed
# Overseer's subnet) and nothing from the other Sandboxes on the sandboxes VNet, a public IP unless on the private network,
# a network interface on that VNet, and the OS disk. The VM is tagged with its owner and the fingerprint of the SSH host
# key its cloud-init installs. Prints the VM's IP address: the public one, or else the private one.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
vm={{VirtualMachine}}
size={{Size}}
username={{Username}}
owner_kind={{OwnerKind}}
owner_id={{OwnerId}}
host_key={{HostKeyFingerprint}}
public_key={{PublicKey}}
custom_data={{CustomDataFile}}
allowed_address={{AllowedAddress}}
private_network={{PrivateNetwork}}

az network nsg create \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$vm-nsg" \
  --output none
az network nsg rule create \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --nsg-name "$vm-nsg" \
  --name ssh-from-overseer \
  --priority 1000 \
  --direction Inbound \
  --access Allow \
  --protocol Tcp \
  --source-address-prefixes "$allowed_address" \
  --destination-port-ranges 22 \
  --output none
# Azure's default rules let in all of the VNet, so this keeps a Sandbox from reaching another one.
az network nsg rule create \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --nsg-name "$vm-nsg" \
  --name deny-other-sandboxes \
  --priority 4000 \
  --direction Inbound \
  --access Deny \
  --protocol '*' \
  --source-address-prefixes VirtualNetwork \
  --destination-port-ranges '*' \
  --output none

if [ "$private_network" != true ]; then
  az network public-ip create \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name "$vm-ip" \
    --sku Standard \
    --allocation-method Static \
    --output none
  az network nic create \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name "$vm-nic" \
    --vnet-name sandboxes \
    --subnet sandboxes \
    --network-security-group "$vm-nsg" \
    --public-ip-address "$vm-ip" \
    --output none
else
  az network nic create \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name "$vm-nic" \
    --vnet-name sandboxes \
    --subnet sandboxes \
    --network-security-group "$vm-nsg" \
    --output none
fi

az vm create \
  --subscription "$subscription_id" \
  --resource-group "$resource_group" \
  --name "$vm" \
  --size "$size" \
  --image Canonical:ubuntu-24_04-lts:server:latest \
  --nics "$vm-nic" \
  --os-disk-name "$vm-disk" \
  --os-disk-size-gb 64 \
  --admin-username "$username" \
  --ssh-key-values "$public_key" \
  --custom-data "$custom_data" \
  --tags "overseer-owner-kind=$owner_kind" "overseer-owner-id=$owner_id" "overseer-host-key=$host_key" \
  --output none

if [ "$private_network" != true ]; then
  az network public-ip show \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name "$vm-ip" \
    --query ipAddress \
    --output tsv
else
  az network nic show \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name "$vm-nic" \
    --query 'ipConfigurations[0].privateIPAddress' \
    --output tsv
fi
