targetScope = 'resourceGroup'

param location string = resourceGroup().location
param prefix string = 'certflow'
param sqlAdminLogin string
@secure()
param sqlAdminPassword string
param mailboxEmail string
param webhookBaseUrl string = ''  // set after first deploy; leave blank initially

@description('False on first deploy (ACR empty). True once all four images are in ACR.')
param imagesPublished bool = false

@description('Shared secret for the publicly-reachable MCP server. Must stay stable across deploys: the Foundry connection stores this value, so regenerating it breaks every agent tool call until the connection is updated.')
@secure()
param mcpApiKey string

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: { location: location, name: '${prefix}-identity' }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { location: location, name: prefix }
}

module keyVault 'modules/key-vault.bicep' = {
  name: 'keyVault'
  params: {
    location: location
    name: 'cf-kv-${uniqueString(resourceGroup().id)}'
    managedIdentityPrincipalId: identity.outputs.principalId
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location: location
    serverName: '${prefix}-sql-${uniqueString(resourceGroup().id)}'
    databaseName: 'certflow'
    adminLogin: sqlAdminLogin
    adminPassword: sqlAdminPassword
    managedIdentityPrincipalId: identity.outputs.principalId
    managedIdentityName: '${prefix}-identity'
    managedIdentityClientId: identity.outputs.clientId
  }
}

module serviceBus 'modules/service-bus.bicep' = {
  name: 'serviceBus'
  params: {
    location: location
    name: '${prefix}-sb-${uniqueString(resourceGroup().id)}'
    managedIdentityPrincipalId: identity.outputs.principalId
  }
}

module acr 'modules/container-registry.bicep' = {
  name: 'acr'
  params: {
    location: location
    name: '${prefix}acr${uniqueString(resourceGroup().id)}'
    managedIdentityPrincipalId: identity.outputs.principalId
  }
}

// Single Azure AI Foundry (AIServices) account hosts both the gpt-4o deployment and
// the project, so no separate Azure OpenAI account is provisioned.
module aiFoundry 'modules/ai-foundry.bicep' = {
  name: 'aiFoundry'
  params: {
    location: location
    accountName: '${prefix}-fdry-${uniqueString(resourceGroup().id, location)}'
    projectName: '${prefix}-project'
    managedIdentityPrincipalId: identity.outputs.principalId
  }
}

module containerApps 'modules/container-apps.bicep' = {
  name: 'containerApps'
  params: {
    location: location
    name: prefix
    managedIdentityId: identity.outputs.identityId
    managedIdentityClientId: identity.outputs.clientId
    acrLoginServer: acr.outputs.acrLoginServer
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
    sqlConnectionString: sql.outputs.connectionString
    serviceBusNamespace: serviceBus.outputs.serviceBusNamespace
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    aiFoundryProjectEndpoint: aiFoundry.outputs.projectEndpoint
    azureOpenAiEndpoint: aiFoundry.outputs.openAiEndpoint
    mailboxEmail: mailboxEmail
    webhookBaseUrl: empty(webhookBaseUrl) ? 'https://placeholder' : webhookBaseUrl
    imagesPublished: imagesPublished
    mcpApiKey: mcpApiKey
  }
}

output acrName string = acr.outputs.acrName
output acrLoginServer string = acr.outputs.acrLoginServer
output apiUrl string = containerApps.outputs.apiUrl
output portalUrl string = containerApps.outputs.portalUrl
// Surfaced so the deploy script can report the endpoint to register as the Foundry MCP tool.
output mcpUrl string = containerApps.outputs.mcpUrl
output aiFoundryProjectEndpoint string = aiFoundry.outputs.projectEndpoint
output aiFoundryAccountName string = aiFoundry.outputs.accountName
output aiFoundryProjectName string = aiFoundry.outputs.projectName
output appInsightsConnectionString string = monitoring.outputs.appInsightsConnectionString
output appInsightsId string = monitoring.outputs.appInsightsId
