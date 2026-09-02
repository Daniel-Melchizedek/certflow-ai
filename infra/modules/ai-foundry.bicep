param location string
param hubName string
param projectName string
param openAIResourceId string
param managedIdentityPrincipalId string

resource hub 'Microsoft.MachineLearningServices/workspaces@2024-04-01' = {
  name: hubName
  location: location
  kind: 'Hub'
  identity: { type: 'SystemAssigned' }
  properties: {
    friendlyName: 'CertFlow AI Hub'
    publicNetworkAccess: 'Enabled'
  }
}

resource openAIConnection 'Microsoft.MachineLearningServices/workspaces/connections@2024-04-01' = {
  parent: hub
  name: 'certflow-openai'
  properties: {
    category: 'AzureOpenAI'
    target: 'https://placeholder.openai.azure.com/'  // updated by deploy.ps1 after openai module deploys
    authType: 'ManagedIdentity'
    isSharedToAll: true
    metadata: { ApiType: 'azure', ResourceId: openAIResourceId }
  }
}

resource project 'Microsoft.MachineLearningServices/workspaces@2024-04-01' = {
  name: projectName
  location: location
  kind: 'Project'
  identity: { type: 'SystemAssigned' }
  properties: {
    friendlyName: 'CertFlow AI Project'
    hubResourceId: hub.id
    publicNetworkAccess: 'Enabled'
  }
}

output projectEndpoint string = 'https://${projectName}.api.azureml.ms'
output hubId string = hub.id
output projectId string = project.id
