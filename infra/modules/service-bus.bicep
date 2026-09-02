param location string
param name string
param managedIdentityPrincipalId string

resource ns 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: name
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
}

var queues = ['inbound-emails', 'email-replies', 'notifications']
resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = [for q in queues: {
  parent: ns
  name: q
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 5
    defaultMessageTimeToLive: 'P1D'
  }
}]

// Grant Managed Identity Service Bus Data Owner on namespace
resource sbRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(ns.id, managedIdentityPrincipalId, '090c5cfd-751d-490a-894a-3ce6f1109419')
  scope: ns
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419') // Azure Service Bus Data Owner
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output serviceBusNamespace string = '${name}.servicebus.windows.net'
