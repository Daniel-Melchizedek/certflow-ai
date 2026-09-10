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
  // 25k TPM, sized from measured usage: a three-exam bulk proposal peaked at 8,565 tokens in a
  // minute, and the confirmation reply a further 2,769.
  //
  // 10k TPM is not enough even though the peak minute fits inside it, because the limit is
  // enforced over a sliding window of seconds rather than a clean per-minute bucket — that burst
  // arrives in roughly 35s, i.e. ~14.7k tokens/min while it is in flight. The headroom here is
  // for the burst rate and for retries, not for the average.
  //
  // Retries are the reason not to cut this fine: a 429 mid-run makes Service Bus redeliver, and
  // each redelivery re-runs the intent agent, so throttling sustains itself until the message
  // dead-letters. GlobalStandard bills per token consumed rather than per unit reserved, so this
  // number does not affect cost — it only bounds the burst and shares regional quota.
  sku: { name: 'GlobalStandard', capacity: 25 }
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
