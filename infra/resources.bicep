param environmentName string
param location string
param aiLocation string
param tags object
param principalId string
param modelName string
param modelVersion string
param modelDeploymentName string
param modelCapacity int
param modelSku string
param functionPlanSku string
param agentName string

var token = toLower(uniqueString(subscription().id, environmentName, location))
var invoiceContainerName = 'invoices'
var invoiceTableName = 'Invoices'

// Built-in role definitions (Foundry roles by ID: they were renamed recently).
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataReader: '76199698-9eea-4c19-bc75-cec21354c6b6'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
  foundryProjectManager: 'eadc314b-1a2d-4efa-be10-5d325db5065e'
}

// ---------------------------------------------------------------- Monitoring
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${token}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// The function sends telemetry with its managed identity; the Foundry project connection below
// (used by the hosted agent) authenticates with the connection string, so key ingestion stays enabled.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${token}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ------------------------------------------------------------------- Storage
// One account: function host storage, the invoice upload container and the result table.
// Shared-key access is off; everything uses Microsoft Entra / managed identity.
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${token}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }

  resource blobService 'blobServices' = {
    name: 'default'
    resource invoices 'containers' = {
      name: invoiceContainerName
    }
  }

  resource tableService 'tableServices' = {
    name: 'default'
    resource invoiceTable 'tables' = {
      name: invoiceTableName
    }
  }
}

// ------------------------------------------- Foundry (model + Document Intelligence)
// An AIServices account serves both the model deployment and Document Intelligence.
resource ai 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: 'ai-${token}'
  location: aiLocation
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: 'ai-${token}'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
}

resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: ai
  name: modelDeploymentName
  sku: {
    name: modelSku
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
  }
}

// The Foundry project hosts the invoice agent (deployed later by `azd deploy`).
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: ai
  name: 'invoice-processing'
  location: aiLocation
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'Invoice Processing'
    description: 'Invoice extraction agent'
  }
  dependsOn: [model]
}

// Lets the platform emit the hosted agent's telemetry to our Application Insights.
resource appInsightsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = {
  parent: project
  name: 'appinsights'
  properties: {
    category: 'AppInsights'
    target: appInsights.id
    authType: 'ApiKey'
    isSharedToAll: true
    credentials: { key: appInsights.properties.ConnectionString }
    metadata: {
      ApiType: 'Azure'
      ResourceId: appInsights.id
    }
  }
}

// ------------------------------------------------------------ Function app
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${token}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: functionPlanSku }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-${token}'
  location: location
  // azd locates the deployment target through this tag (service name in azure.yaml).
  tags: union(tags, { 'azd-service-name': 'api' })
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      alwaysOn: true // keeps the host warm so the Event Grid webhook answers without a cold start
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        // Identity-based host storage
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        // Identity-based "InvoiceStorage" connection used by the blob trigger
        { name: 'InvoiceStorage__blobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'InvoiceStorage__queueServiceUri', value: storage.properties.primaryEndpoints.queue }
        // Where the hosted agent lives (bound to InvoiceOptions)
        { name: 'Invoice__FoundryProjectEndpoint', value: 'https://${ai.name}.services.ai.azure.com/api/projects/${project.name}' }
        { name: 'Invoice__AgentName', value: agentName }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'Authorization=AAD' }
      ]
    }
  }
}

// Blob events for the Event Grid blob trigger. Storage publishes them by itself (no keys or roles needed).
// The subscription targets the deployed function's webhook, so the api postdeploy hook
// (hooks/subscribe-blob-events) creates it.
resource storageEvents 'Microsoft.EventGrid/systemTopics@2024-06-01-preview' = {
  name: 'evgt-${token}'
  location: location
  tags: tags
  properties: {
    source: storage.id
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

// ---------------------------------------------------------------------- RBAC
// The function only triggers: it needs its trigger storage and to publish telemetry.
// Everything else is scoped to the AGENT, not the project, and is granted by the azd postdeploy hook
// because the agent (and its identity) only exist after `azd deploy`:
//   - the agent's own identity: blob read, table write, Document Intelligence, model calls
//   - this function's right to call that one agent (Foundry Agent Consumer at agent scope)
var functionStorageRoleIds = [
  roles.storageBlobDataOwner
  roles.storageQueueDataContributor
]

resource functionStorageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in functionStorageRoleIds: {
    name: guid(storage.id, functionApp.id, roleId)
    scope: storage
    properties: {
      principalId: functionApp.identity.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]

resource functionMetricsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, functionApp.id, roles.monitoringMetricsPublisher)
  scope: appInsights
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.monitoringMetricsPublisher)
  }
}

// Deploying user: deploy and call the agent, upload invoices, read the result table.
resource userProjectManagerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(project.id, principalId, roles.foundryProjectManager)
  scope: project
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryProjectManager)
  }
}

resource userBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(storage.id, principalId, roles.storageBlobDataContributor)
  scope: storage
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
  }
}

resource userTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(storage.id, principalId, roles.storageTableDataReader)
  scope: storage
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataReader)
  }
}

output functionAppName string = functionApp.name
output storageEventsTopicName string = storageEvents.name
output functionPrincipalId string = functionApp.identity.principalId
output storageAccountName string = storage.name
output storageAccountId string = storage.id
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output tableEndpoint string = storage.properties.primaryEndpoints.table
output invoiceContainer string = invoiceContainerName
output invoiceTable string = invoiceTableName
output aiAccountId string = ai.id
output documentIntelligenceEndpoint string = ai.properties.endpoint
output openAIEndpoint string = 'https://${ai.name}.openai.azure.com/'
output foundryProjectName string = project.name
output foundryProjectId string = project.id
output foundryProjectEndpoint string = 'https://${ai.name}.services.ai.azure.com/api/projects/${project.name}'
