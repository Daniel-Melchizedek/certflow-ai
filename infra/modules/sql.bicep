param location string
param serverName string
param databaseName string
param adminLogin string
@secure()
param adminPassword string
param managedIdentityPrincipalId string
param managedIdentityName string
param managedIdentityClientId string

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: serverName
  location: location
  properties: {
    administratorLogin: adminLogin
    administratorLoginPassword: adminPassword
    minimalTlsVersion: '1.2'
  }
}

// Set the Managed Identity as the AAD admin — required for Managed Identity auth
resource sqlAadAdmin 'Microsoft.Sql/servers/administrators@2023-05-01-preview' = {
  parent: sqlServer
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: managedIdentityName
    sid: managedIdentityPrincipalId
    tenantId: subscription().tenantId
  }
}

resource firewall 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: { name: 'Basic', tier: 'Basic', capacity: 5 }
  properties: {}
}

output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
// User Id must carry the user-assigned identity's client ID — SqlClient does not
// read AZURE_CLIENT_ID, so without it the driver cannot pick the right identity.
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${databaseName};Authentication=Active Directory Managed Identity;User Id=${managedIdentityClientId};Encrypt=True;'
