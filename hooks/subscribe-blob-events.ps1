# azd postdeploy hook for the function app (PowerShell twin of subscribe-blob-events.sh).
# Connects the storage account's blob events to the Event Grid blob trigger:
#   system topic (Bicep) -> event subscription (this hook, BlobCreated in the invoices container) -> function webhook
# Needs the deployed function: the webhook validation and the blobs_extension key only exist then.
# Safe to run again.
$ErrorActionPreference = 'Stop'

foreach ($v in 'AZURE_RESOURCE_GROUP', 'AZURE_FUNCTION_APP_NAME', 'AZURE_EVENTGRID_SYSTEM_TOPIC', 'INVOICE_CONTAINER') {
    if (-not [Environment]::GetEnvironmentVariable($v)) {
        Write-Error "subscribe-blob-events: environment value $v is missing (run 'azd provision' first)."
    }
}

$functionName = 'ProcessInvoice'
$subscriptionName = 'invoice-blob-created'

Write-Host "Reading the blob extension key of '$env:AZURE_FUNCTION_APP_NAME'..."
$key = ''
for ($attempt = 1; $attempt -le 12; $attempt++) {
    $key = az functionapp keys list -g $env:AZURE_RESOURCE_GROUP -n $env:AZURE_FUNCTION_APP_NAME `
        --query 'systemKeys.blobs_extension' -o tsv 2>$null
    if ($key) { break }
    Write-Host "  not there yet (attempt $attempt/12), waiting 10s..."
    Start-Sleep -Seconds 10
}
if (-not $key) {
    Write-Error "subscribe-blob-events: the function host has no 'blobs_extension' system key. Check that the function started (Log stream / Application Insights)."
}

$hostName = az functionapp show -g $env:AZURE_RESOURCE_GROUP -n $env:AZURE_FUNCTION_APP_NAME --query defaultHostName -o tsv
$endpoint = "https://$hostName/runtime/webhooks/blobs?functionName=Host.Functions.$functionName&code=$key"
# az is a .cmd on Windows: cmd.exe would split the URL at '&', so it is passed wrapped in literal quotes below.

Write-Host "Subscribing '$functionName' to BlobCreated events of container '$env:INVOICE_CONTAINER'..."
# Event Grid validates the webhook when the subscription is created; right after a deploy the host may still be starting.
for ($attempt = 1; $attempt -le 6; $attempt++) {
    az eventgrid system-topic event-subscription create `
        -g $env:AZURE_RESOURCE_GROUP `
        --system-topic-name $env:AZURE_EVENTGRID_SYSTEM_TOPIC `
        -n $subscriptionName `
        --endpoint-type webhook `
        --endpoint "`"$endpoint`"" `
        --included-event-types Microsoft.Storage.BlobCreated `
        --subject-begins-with "/blobServices/default/containers/$($env:INVOICE_CONTAINER)/" `
        --only-show-errors -o none
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'Done. New invoices now trigger the function within seconds.'
        exit 0
    }
    Write-Host "  webhook validation failed (attempt $attempt/6), waiting 15s..."
    Start-Sleep -Seconds 15
}

Write-Error 'subscribe-blob-events: could not create the Event Grid subscription. Run the hook again once the function is up.'
