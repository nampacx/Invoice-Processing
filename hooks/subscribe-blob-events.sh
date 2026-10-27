#!/usr/bin/env bash
# azd postdeploy hook for the function app (api).
#
# Connects the storage account's blob events to the Event Grid blob trigger:
#   system topic (Bicep, source = storage account)
#     -> event subscription (this hook): BlobCreated in the invoices container
#     -> https://<function>/runtime/webhooks/blobs?functionName=Host.Functions.ProcessInvoice&code=<blobs_extension key>
#
# This can't live in Bicep: the webhook only answers Event Grid's validation handshake, and the
# blobs_extension key only exists, once the function code is deployed and the host is running.
#
# Safe to run again: the subscription is created or updated in place.
set -euo pipefail

for v in AZURE_RESOURCE_GROUP AZURE_FUNCTION_APP_NAME AZURE_EVENTGRID_SYSTEM_TOPIC INVOICE_CONTAINER; do
  if [ -z "${!v:-}" ]; then echo "subscribe-blob-events: environment value $v is missing (run 'azd provision' first)." >&2; exit 1; fi
done

function_name="ProcessInvoice"
subscription_name="invoice-blob-created"

echo "Reading the blob extension key of '${AZURE_FUNCTION_APP_NAME}'..."
key=""
for attempt in $(seq 1 12); do
  key=$(az functionapp keys list -g "$AZURE_RESOURCE_GROUP" -n "$AZURE_FUNCTION_APP_NAME" \
    --query "systemKeys.blobs_extension" -o tsv 2>/dev/null || true)
  if [ -n "$key" ]; then break; fi
  echo "  not there yet (attempt $attempt/12), waiting 10s..."
  sleep 10
done
if [ -z "$key" ]; then
  echo "subscribe-blob-events: the function host has no 'blobs_extension' system key. Check that the function started (Log stream / Application Insights)." >&2
  exit 1
fi

host=$(az functionapp show -g "$AZURE_RESOURCE_GROUP" -n "$AZURE_FUNCTION_APP_NAME" --query defaultHostName -o tsv)
endpoint="https://${host}/runtime/webhooks/blobs?functionName=Host.Functions.${function_name}&code=${key}"

echo "Subscribing '${function_name}' to BlobCreated events of container '${INVOICE_CONTAINER}'..."
# Event Grid validates the webhook when the subscription is created; right after a deploy the host may still be starting.
for attempt in $(seq 1 6); do
  if az eventgrid system-topic event-subscription create \
      -g "$AZURE_RESOURCE_GROUP" \
      --system-topic-name "$AZURE_EVENTGRID_SYSTEM_TOPIC" \
      -n "$subscription_name" \
      --endpoint-type webhook \
      --endpoint "$endpoint" \
      --included-event-types Microsoft.Storage.BlobCreated \
      --subject-begins-with "/blobServices/default/containers/${INVOICE_CONTAINER}/" \
      --only-show-errors -o none; then
    echo "Done. New invoices now trigger the function within seconds."
    exit 0
  fi
  echo "  webhook validation failed (attempt $attempt/6), waiting 15s..."
  sleep 15
done

echo "subscribe-blob-events: could not create the Event Grid subscription. Run the hook again once the function is up." >&2
exit 1
