param vnetName string = 'vnet-codymo-lease'
param subnetName string = 'snet-videotranslator-integration'
param addressPrefix string = '10.20.3.0/26'

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: vnetName
}
resource subnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: vnet
  name: subnetName
  properties: {
    addressPrefix: addressPrefix
    delegations: [
      {
        name: 'appservice'
        properties: { serviceName: 'Microsoft.Web/serverFarms' }
      }
    ]
  }
}
output subnetId string = subnet.id
