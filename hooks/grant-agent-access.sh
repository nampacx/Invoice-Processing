#!/usr/bin/env bash
# azd postdeploy hook for the hosted agent.
#
# Everything here is scoped to the AGENT, never to the Foundry project or its shared identity.
# The agent only exists after `azd deploy`, so the Bicep template cannot grant this up front.
#
# 1) The agent's own Microsoft Entra identity gets exactly four roles:
#      Storage Blob Data Reader        on the invoices container   (get_invoice_file)
#      Storage Table Data Contributor  on the Invoices table       (save_invoice)
#      Cognitive Services User         on the Foundry account      (Document Intelligence)
#      Cognitive Services OpenAI User  on the Foundry account      (model calls)
# 2) The function app's identity gets "Foundry Agent Consumer" on this one agent (agent scope),
#    which is what allows it to call the agent endpoint.
#
# Safe to run again: every deploy creates a new agent version, existing assignments are skipped.
# Needs: az CLI signed in as someone who can create role assignments (e.g. Owner on the resource group).
set -euo pipefail

for v in AZURE_AI_PROJECT_ENDPOINT AZURE_AI_ACCOUNT_ID AZURE_FOUNDRY_PROJECT INVOICE_AGENT_NAME \
         AZURE_STORAGE_ACCOUNT_ID INVOICE_CONTAINER INVOICE_TABLE AZURE_FUNCTION_PRINCIPAL_ID; do
  if [ -z "${!v:-}" ]; then echo "grant-agent-access: environment value $v is missing (run 'azd provision' first)." >&2; exit 1; fi
done

echo "Looking up the identity of agent '${INVOICE_AGENT_NAME}'..."
agent_principal=""
for attempt in $(seq 1 12); do
  agent_principal=$(az rest --method get --resource https://ai.azure.com \
    --url "${AZURE_AI_PROJECT_ENDPOINT}/agents/${INVOICE_AGENT_NAME}?api-version=v1" \
    --query "instance_identity.principal_id" -o tsv 2>/dev/null || true)
  if [ -n "$agent_principal" ]; then break; fi
  echo "  not there yet (attempt ${attempt}/12), waiting 10s..."
  sleep 10
done
if [ -z "$agent_principal" ]; then
  echo "grant-agent-access: could not read instance_identity.principal_id of agent '${INVOICE_AGENT_NAME}'." >&2
  echo "Check with 'azd ai agent show', then grant the roles by hand (see README)." >&2
  exit 1
fi
echo "Agent identity: ${agent_principal}"

# Roles are addressed by ID: the Foundry roles were renamed recently, the IDs did not change.
BLOB_READER=2a2b9908-6ea1-4ae2-8e65-a410df84e7d1
TABLE_CONTRIBUTOR=0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3
COGNITIVE_SERVICES_USER=a97b65f3-24c7-4388-baec-2e87135dc908
COGNITIVE_SERVICES_OPENAI_USER=5e0bd9bd-7b93-4f28-af87-19fc36ad61bd
FOUNDRY_AGENT_CONSUMER=eed3b665-ab3a-47b6-8f48-c9382fb1dad6

grant() { # grant <principal-id> <role-id> <scope> <description>
  local principal="$1" role="$2" scope="$3" label="$4" existing
  existing=$(az role assignment list --scope "$scope" \
    --query "[?principalId=='${principal}' && ends_with(roleDefinitionId, '${role}')] | [0].id" -o tsv)
  if [ -n "$existing" ]; then
    echo "  = ${label} (already assigned)"
  else
    az role assignment create --assignee-object-id "$principal" --assignee-principal-type ServicePrincipal \
      --role "$role" --scope "$scope" --only-show-errors -o none
    echo "  + ${label}"
  fi
}

echo "Granting the agent identity its data access..."
grant "$agent_principal" "$BLOB_READER"                    "${AZURE_STORAGE_ACCOUNT_ID}/blobServices/default/containers/${INVOICE_CONTAINER}" "Blob Data Reader on container '${INVOICE_CONTAINER}'"
grant "$agent_principal" "$TABLE_CONTRIBUTOR"              "${AZURE_STORAGE_ACCOUNT_ID}/tableServices/default/tables/${INVOICE_TABLE}"         "Table Data Contributor on table '${INVOICE_TABLE}'"
grant "$agent_principal" "$COGNITIVE_SERVICES_USER"        "${AZURE_AI_ACCOUNT_ID}"                                                           "Cognitive Services User on the Foundry account"
grant "$agent_principal" "$COGNITIVE_SERVICES_OPENAI_USER" "${AZURE_AI_ACCOUNT_ID}"                                                           "Cognitive Services OpenAI User on the Foundry account"

echo "Letting the function call this one agent (agent scope)..."
agent_scope="${AZURE_AI_ACCOUNT_ID}/projects/${AZURE_FOUNDRY_PROJECT}/agents/${INVOICE_AGENT_NAME}"
grant "$AZURE_FUNCTION_PRINCIPAL_ID" "$FOUNDRY_AGENT_CONSUMER" "$agent_scope" "Foundry Agent Consumer on agent '${INVOICE_AGENT_NAME}' for the function app"

echo "Done. New role assignments can take a few minutes to take effect - the first invoice may need a retry."
