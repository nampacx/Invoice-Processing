targetScope = 'subscription'

@minLength(1)
@maxLength(32)
@description('Name of the azd environment; used to derive resource names.')
param environmentName string

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Location of the Foundry resource if the model is not available in the primary location.')
param aiLocation string = location

@description('Object ID of the deploying user. Gets access to upload invoices and read the result table. Empty to skip.')
param principalId string = ''

@description('Model to deploy to Foundry (prompted by azd), e.g. the name shown in the Foundry model catalog.')
param modelName string

@description('Version of the model to deploy (prompted by azd).')
param modelVersion string

@description('Name of the model deployment the app calls. Defaults to the model name.')
param modelDeploymentName string = modelName

@description('Capacity of the model deployment (thousands of tokens per minute for Standard SKUs).')
param modelCapacity int = 50

@allowed(['GlobalStandard', 'Standard', 'DataZoneStandard'])
param modelSku string = 'GlobalStandard'

@description('App Service plan SKU. The polling blob trigger needs a dedicated or Elastic Premium plan (Flex Consumption only supports Event Grid blob triggers).')
param functionPlanSku string = 'B1'

var tags = { 'azd-env-name': environmentName }

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    environmentName: environmentName
    location: location
    aiLocation: aiLocation
    tags: tags
    principalId: principalId
    modelName: modelName
    modelVersion: modelVersion
    modelDeploymentName: modelDeploymentName
    modelCapacity: modelCapacity
    modelSku: modelSku
    functionPlanSku: functionPlanSku
  }
}

// Consumed by azd (and handy for `azd env get-values`).
output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_FUNCTION_APP_NAME string = resources.outputs.functionAppName
output AZURE_STORAGE_ACCOUNT string = resources.outputs.storageAccountName
output INVOICE_CONTAINER string = resources.outputs.invoiceContainer
output INVOICE_TABLE string = resources.outputs.invoiceTable
output AZURE_AI_ENDPOINT string = resources.outputs.aiEndpoint
output AZURE_FOUNDRY_PROJECT string = resources.outputs.foundryProjectName
output AZURE_MODEL_DEPLOYMENT string = modelDeploymentName
