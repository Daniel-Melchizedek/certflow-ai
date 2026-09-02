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
param mailboxEmail string
param webhookBaseUrl string

var envVars = [
  { name: 'ConnectionStrings__CertFlow', value: sqlConnectionString }
  { name: 'ServiceBusNamespace', value: serviceBusNamespace }
  { name: 'MailboxEmail', value: mailboxEmail }
  { name: 'WebhookBaseUrl', value: webhookBaseUrl }
  { name: 'AiFoundryProjectEndpoint', value: aiFoundryProjectEndpoint }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
  { name: 'AZURE_EXPERIMENTAL_ENABLE_GENAI_TRACING', value: 'true' }
  { name: 'OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT', value: 'true' }
  { name: 'AZURE_CLIENT_ID', value: managedIdentityClientId }
]

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
    }
    template: {
      containers: [{
        name: 'certflow-api'
        image: '${acrLoginServer}/certflow-api:latest'
        env: envVars
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
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
      ingress: { external: false, targetPort: 8080 }
      registries: [{ server: acrLoginServer, identity: managedIdentityId }]
    }
    template: {
      containers: [{
        name: 'certflow-mcp'
        image: '${acrLoginServer}/certflow-mcpserver:latest'
        env: envVars
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
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
    }
    template: {
      containers: [{
        name: 'certflow-worker'
        image: '${acrLoginServer}/certflow-worker:latest'
        env: concat(envVars, [{ name: 'McpServerBaseUrl', value: 'https://${mcpApp.properties.configuration.ingress.fqdn}' }])
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
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
        image: '${acrLoginServer}/certflow-portal:latest'
        env: concat(envVars, [{ name: 'ApiBaseUrl', value: 'https://${apiApp.properties.configuration.ingress.fqdn}' }])
        resources: { cpu: '0.25', memory: '0.5Gi' }
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
    }
  }
}

output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
output portalUrl string = 'https://${portalApp.properties.configuration.ingress.fqdn}'
output mcpInternalUrl string = 'https://${mcpApp.properties.configuration.ingress.fqdn}'
