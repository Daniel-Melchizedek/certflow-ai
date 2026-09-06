param location string
param accountName string
param projectName string
param managedIdentityPrincipalId string

// Azure AI Foundry resource (new model). Azure.AI.Projects >= 1.0.0-beta.9 talks only to
// this resource type via a https://<account>.services.ai.azure.com/api/projects/<project>
// endpoint. The older MachineLearningServices Hub/Project pair has no project endpoint
// and is not supported by the SDK version this solution uses.
resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    // Both are load-bearing: allowProjectManagement enables the projects child resource,
    // customSubDomainName produces the services.ai.azure.com hostname the SDK requires.
    allowProjectManagement: true
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
  }
}

// Model deployments live on the Foundry account itself — no separate Azure OpenAI
// account or workspace connection is needed.
resource gpt4oDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: 'gpt-4o'
  sku: { name: 'GlobalStandard', capacity: 10 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4o', version: '2024-11-20' }
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'CertFlow AI Project'
    description: 'Intent & Identity + Policy & Scheduling agents for CertFlow AI'
  }
  dependsOn: [gpt4oDeployment]
}

// Azure AI User — lets the Managed Identity call agents and model deployments.
resource foundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, managedIdentityPrincipalId, '53ca6127-db72-4b80-b1b0-d745d6d5456d')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Cognitive Services OpenAI User — required for the chat completions data plane.
resource openAIUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, managedIdentityPrincipalId, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output projectEndpoint string = 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}'
// Data-plane host for chat completions against the account's own deployments.
output openAiEndpoint string = 'https://${accountName}.openai.azure.com/'
output accountEndpoint string = account.properties.endpoint
output accountId string = account.id
