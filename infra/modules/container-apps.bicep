param location string
param name string
param managedIdentityId string
param managedIdentityClientId string
param acrLoginServer string
param logAnalyticsWorkspaceId string
param sqlConnectionString string
param serviceBusNamespace string
param appInsightsConnectionString string
param aiFoundryProjectEndpoint string
param azureOpenAiEndpoint string
param contentSafetyEndpoint string
param mailboxEmail string
param webhookBaseUrl string

@description('Shared secret that Foundry and our own services present to the MCP server. Required: the MCP server refuses to start without it, because its ingress is public and it exposes write tools.')
@secure()
param mcpApiKey string

@description('Set false on the very first deploy (ACR is empty, so a public placeholder is used). Set true once images have been pushed so redeploys keep the real images.')
param imagesPublished bool = false

// On first deploy ACR has no images yet, so every app starts on a public placeholder.
// After `az acr build` has pushed all four images, redeploy with imagesPublished=true.
var placeholder = 'mcr.microsoft.com/dotnet/samples:aspnetapp'
var apiImage = imagesPublished ? '${acrLoginServer}/certflow-api:latest' : placeholder
var mcpImage = imagesPublished ? '${acrLoginServer}/certflow-mcpserver:latest' : placeholder
var workerImage = imagesPublished ? '${acrLoginServer}/certflow-worker:latest' : placeholder
var portalImage = imagesPublished ? '${acrLoginServer}/certflow-portal:latest' : placeholder

var envVars = [
  { name: 'ConnectionStrings__CertFlow', value: sqlConnectionString }
  { name: 'ServiceBusNamespace', value: serviceBusNamespace }
  { name: 'MailboxEmail', value: mailboxEmail }
  { name: 'WebhookBaseUrl', value: webhookBaseUrl }
  { name: 'AiFoundryProjectEndpoint', value: aiFoundryProjectEndpoint }
  { name: 'AzureOpenAiEndpoint', value: azureOpenAiEndpoint }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
  { name: 'AZURE_EXPERIMENTAL_ENABLE_GENAI_TRACING', value: 'true' }
  { name: 'OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT', value: 'true' }
  { name: 'AZURE_CLIENT_ID', value: managedIdentityClientId }
  { name: 'FoundryMcpConnectionId', value: 'certflow-mcp' }
  // Work IQ Calendar connection name (created via Foundry portal — Managed OAuth, Identity Passthrough).
  // The Slot Advisor agent uses this to access the candidate's Microsoft 365 calendar.
  { name: 'WorkIQConnectionId', value: 'WorkIQCalendar' }
  { name: 'ContentSafetyEndpoint', value: contentSafetyEndpoint }
]

// Declared in Bicep rather than set imperatively after deployment. ARM replaces `secrets` and
// `env` wholesale, so a secret that only exists because a post-deploy script added it is silently
// dropped by the next template deployment — which is exactly the drift the portal's
// aad-client-secret already suffers from.
var mcpKeySecret = [{ name: 'mcp-api-key', value: mcpApiKey }]
var mcpKeyEnv = [{ name: 'McpApiKey', secretRef: 'mcp-api-key' }]

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
        sharedKey: listKeys(logAnalyticsWorkspaceId, '2023-09-01').primarySharedKey
      }
    }
  }
}

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${name}-api'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${managedIdentityId}': {} } }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: { external: true, targetPort: 8080 }
      registries: [{ server: acrLoginServer, identity: managedIdentityId }]
      secrets: mcpKeySecret
    }
    template: {
      containers: [{
        name: 'certflow-api'
        image: apiImage
        // McpServerBaseUrl is required here, not optional. The API registers
        // AgentRegistrationService unconditionally but AgentOrchestrator only when this value is
        // present, so without it POST /admin/agents/register fails to resolve its dependencies —
        // and that endpoint is what creates the agents in Foundry during deployment. A fresh
        // environment would come up with no agents at all and a 500 from the deploy script.
        env: concat(envVars, mcpKeyEnv, [{ name: 'McpServerBaseUrl', value: 'https://${mcpApp.properties.configuration.ingress.fqdn}' }])
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      // Not scaled to zero: this app serves the Graph change-notification webhook, which
      // must answer within 3 seconds or Graph starts dropping notifications. A cold start
      // takes far longer than that budget on its own.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource mcpApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${name}-mcp'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${managedIdentityId}': {} } }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      // Public so Azure AI Foundry can reach this as a remote MCP tool. Foundry is a
      // multi-tenant service whose egress addresses are not enumerable, so ipSecurityRestrictions
      // cannot substitute for auth — the API-key check in the app is what protects it. Private
      // ingress would need Standard agent setup on a BYO VNet, which this environment is not.
      ingress: { external: true, targetPort: 8080 }
      registries: [{ server: acrLoginServer, identity: managedIdentityId }]
      secrets: mcpKeySecret
    }
    template: {
      containers: [{
        name: 'certflow-mcp'
        image: mcpImage
        env: concat(envVars, mcpKeyEnv)
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      // Not scaled to zero any more: Foundry caps a non-streaming MCP tool call at 100s, and a
      // cold start inside that budget makes the first call of an idle period flaky.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource workerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${name}-worker'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${managedIdentityId}': {} } }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      registries: [{ server: acrLoginServer, identity: managedIdentityId }]
      secrets: mcpKeySecret
    }
    template: {
      containers: [{
        name: 'certflow-worker'
        image: workerImage
        env: concat(envVars, mcpKeyEnv, [{ name: 'McpServerBaseUrl', value: 'https://${mcpApp.properties.configuration.ingress.fqdn}' }])
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource portalApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${name}-portal'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${managedIdentityId}': {} } }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: { external: true, targetPort: 8080 }
      registries: [{ server: acrLoginServer, identity: managedIdentityId }]
    }
    template: {
      containers: [{
        name: 'certflow-portal'
        image: portalImage
        env: concat(envVars, [{ name: 'ApiBaseUrl', value: 'https://${apiApp.properties.configuration.ingress.fqdn}' }])
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
    }
  }
}

output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
output portalUrl string = 'https://${portalApp.properties.configuration.ingress.fqdn}'
// No longer "internal" — this is the URL registered as the remote MCP server endpoint in Foundry.
output mcpUrl string = 'https://${mcpApp.properties.configuration.ingress.fqdn}'
