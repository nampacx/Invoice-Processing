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

var token = toLower(uniqueString(subscription().id, environmentName, location))
var invoiceContainerName = 'invoices'
var invoiceTableName = 'Invoices'

// Built-in role definitions
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-4d4b-8d4f-9cd2cef4b8a1'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  cognitiveServicesUser: 'a97b65f3-24c7-4388-baec-2e87135dc908'
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
// An AIServices account serves both the model deployment (Azure OpenAI API) and Document Intelligence.
resource ai 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: 'ai-${token}'
  location: aiLocation
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: 'ai-${token}'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
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
      alwaysOn: true // required for the polling blob trigger to keep scanning
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        // Identity-based host storage
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        // Identity-based "InvoiceStorage" connection used by the blob trigger and the table writer
        { name: 'InvoiceStorage__blobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'InvoiceStorage__queueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'InvoiceStorage__tableServiceUri', value: storage.properties.primaryEndpoints.table }
        // App configuration (bound to InvoiceOptions)
        { name: 'Invoice__OpenAIEndpoint', value: 'https://${ai.name}.openai.azure.com/' }
        { name: 'Invoice__DocumentIntelligenceEndpoint', value: ai.properties.endpoint }
        { name: 'Invoice__ModelDeployment', value: model.name }
        { name: 'Invoice__TableName', value: invoiceTableName }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// ---------------------------------------------------------------------- RBAC
var functionStorageRoleIds = [
  roles.storageBlobDataOwner
  roles.storageQueueDataContributor
  roles.storageTableDataContributor
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

resource functionOpenAIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(ai.id, functionApp.id, roles.cognitiveServicesOpenAIUser)
  scope: ai
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAIUser)
  }
}

resource functionDocIntelRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(ai.id, functionApp.id, roles.cognitiveServicesUser)
  scope: ai
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesUser)
  }
}

// Deploying user: upload invoices and inspect the table.
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
  name: guid(storage.id, principalId, roles.storageTableDataContributor)
  scope: storage
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataContributor)
  }
}

output functionAppName string = functionApp.name
output storageAccountName string = storage.name
output invoiceContainer string = invoiceContainerName
output invoiceTable string = invoiceTableName
output aiEndpoint string = ai.properties.endpoint
