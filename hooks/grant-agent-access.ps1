# azd postdeploy hook for the hosted agent (PowerShell twin of grant-agent-access.sh).
# Agent-scoped access only:
#   - the agent's own identity: Blob Data Reader (container), Table Data Contributor (table),
#     Cognitive Services User + Cognitive Services OpenAI User (Foundry account)
#   - the function app: Foundry Agent Consumer on this one agent (agent scope)
# Safe to run again.
$ErrorActionPreference = 'Stop'

foreach ($v in 'AZURE_AI_PROJECT_ENDPOINT', 'AZURE_AI_ACCOUNT_ID', 'AZURE_FOUNDRY_PROJECT', 'INVOICE_AGENT_NAME',
               'AZURE_STORAGE_ACCOUNT_ID', 'INVOICE_CONTAINER', 'INVOICE_TABLE', 'AZURE_FUNCTION_PRINCIPAL_ID') {
    if (-not [Environment]::GetEnvironmentVariable($v)) {
        Write-Error "grant-agent-access: environment value $v is missing (run 'azd provision' first)."
    }
}

Write-Host "Looking up the identity of agent '$env:INVOICE_AGENT_NAME'..."
$agentPrincipal = ''
for ($attempt = 1; $attempt -le 12; $attempt++) {
    $agentPrincipal = az rest --method get --resource https://ai.azure.com `
        --url "$env:AZURE_AI_PROJECT_ENDPOINT/agents/$($env:INVOICE_AGENT_NAME)?api-version=v1" `
        --query 'instance_identity.principal_id' -o tsv 2>$null
    if ($agentPrincipal) { break }
    Write-Host "  not there yet (attempt $attempt/12), waiting 10s..."
    Start-Sleep -Seconds 10
}
if (-not $agentPrincipal) {
    Write-Error "grant-agent-access: could not read instance_identity.principal_id of agent '$env:INVOICE_AGENT_NAME'. Check with 'azd ai agent show', then grant the roles by hand (see README)."
}
Write-Host "Agent identity: $agentPrincipal"

# Roles are addressed by ID: the Foundry roles were renamed recently, the IDs did not change.
$BlobReader = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
$TableContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
$CognitiveServicesUser = 'a97b65f3-24c7-4388-baec-2e87135dc908'
$CognitiveServicesOpenAIUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
$FoundryAgentConsumer = 'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'

function Grant([string]$Principal, [string]$Role, [string]$Scope, [string]$Label) {
    $existing = az role assignment list --scope $Scope `
        --query "[?principalId=='$Principal' && ends_with(roleDefinitionId, '$Role')] | [0].id" -o tsv
    if ($existing) {
        Write-Host "  = $Label (already assigned)"
    } else {
        az role assignment create --assignee-object-id $Principal --assignee-principal-type ServicePrincipal `
            --role $Role --scope $Scope --only-show-errors -o none
        if ($LASTEXITCODE -ne 0) { Write-Error "grant-agent-access: could not assign $Label." }
        Write-Host "  + $Label"
    }
}

Write-Host 'Granting the agent identity its data access...'
Grant $agentPrincipal $BlobReader "$env:AZURE_STORAGE_ACCOUNT_ID/blobServices/default/containers/$env:INVOICE_CONTAINER" "Blob Data Reader on container '$env:INVOICE_CONTAINER'"
Grant $agentPrincipal $TableContributor "$env:AZURE_STORAGE_ACCOUNT_ID/tableServices/default/tables/$env:INVOICE_TABLE" "Table Data Contributor on table '$env:INVOICE_TABLE'"
Grant $agentPrincipal $CognitiveServicesUser $env:AZURE_AI_ACCOUNT_ID 'Cognitive Services User on the Foundry account'
Grant $agentPrincipal $CognitiveServicesOpenAIUser $env:AZURE_AI_ACCOUNT_ID 'Cognitive Services OpenAI User on the Foundry account'

Write-Host 'Letting the function call this one agent (agent scope)...'
$agentScope = "$env:AZURE_AI_ACCOUNT_ID/projects/$env:AZURE_FOUNDRY_PROJECT/agents/$env:INVOICE_AGENT_NAME"
Grant $env:AZURE_FUNCTION_PRINCIPAL_ID $FoundryAgentConsumer $agentScope "Foundry Agent Consumer on agent '$env:INVOICE_AGENT_NAME' for the function app"

Write-Host 'Done. New role assignments can take a few minutes to take effect - the first invoice may need a retry.'
