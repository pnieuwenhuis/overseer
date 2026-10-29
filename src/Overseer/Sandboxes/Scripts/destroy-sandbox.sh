# Deletes a Sandbox's VM and everything create-sandbox.sh made with it, in an order in which nothing deleted is still
# in use: the VM, its OS disk, its network interface, then the public IP and network security group the interface
# used. Azure deletes what doesn't exist without complaint, so this also clears away a half-created Sandbox.
set -euo pipefail

subscription_id={{SubscriptionId}}
resource_group={{ResourceGroup}}
vm={{VirtualMachine}}

providers="/subscriptions/$subscription_id/resourceGroups/$resource_group/providers"
for id in \
  "$providers/Microsoft.Compute/virtualMachines/$vm" \
  "$providers/Microsoft.Compute/disks/$vm-disk" \
  "$providers/Microsoft.Network/networkInterfaces/$vm-nic" \
  "$providers/Microsoft.Network/publicIPAddresses/$vm-ip" \
  "$providers/Microsoft.Network/networkSecurityGroups/$vm-nsg"; do
  az resource delete --ids "$id" --output none
done
