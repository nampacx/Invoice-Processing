# Invoice Processing

Upload an invoice to Blob Storage → an Azure Function (blob trigger) runs an agent → the result lands in Table Storage.

```
 invoices/ container ──► ProcessInvoice (blob trigger)
                              │
                              ▼
                  InvoiceAgent (Microsoft Agent Framework, model on Azure AI Foundry)
                              │  tool call
                              ▼
        read_invoice_document = Document Intelligence (prebuilt-invoice)
                              + deterministic JSON reduction (InvoiceJsonReducer)
                              │
                              ▼  structured output (InvoiceExtraction)
                  InvoiceTableWriter ──► Table "Invoices"
```

## Components
| Path | Purpose |
|---|---|
| `Functions/ProcessInvoiceFunction.cs` | Blob trigger on `invoices/{name}`; rethrows on failure so the trigger's retry/poison handling applies |
| `Agents/InvoiceAgent.cs` | `ChatClientAgent` on an Azure OpenAI/Foundry chat client; returns `InvoiceExtraction` via `RunAsync<T>` |
| `Tools/InvoiceDocumentTool.cs` | The one agent tool: Document Intelligence + reducer. Bound to the current blob, so no file content passes through the model |
| `Tools/InvoiceJsonReducer.cs` | Drops pages/words/polygons/spans, flattens fields to values, keeps `confidence` only when < 0.9 |
| `Storage/InvoiceTableWriter.cs` | One partition per blob: row `Invoice` (transaction + vendor) and rows `Line-0001…` (line items), written as one batch; re-uploads overwrite |

## Observability (Application Insights)
Set `APPLICATIONINSIGHTS_CONNECTION_STRING`. You get:
- **Logs** (`traces`): `Invoice blob received`, `Agent tool call -> read_invoice_document(...)`, tool duration/size, Document Intelligence raw→reduced size, `model requested tool …`, token usage, agent warnings, failures.
- **Traces** (`dependencies`): Functions host spans, plus `invoke_agent InvoiceExtractor`, one `chat <deployment>` span per model call (tokens, finish reason) and `execute_tool` spans, from the `InvoiceProcessing.Agent` source.
- To also record prompts/tool payloads in spans, set `EnableSensitiveData = true` on the two `UseOpenTelemetry` calls in `InvoiceAgent` (contains invoice data — off by default).

Handy query: `traces | where message has "Agent tool" or message has "Agent run" | order by timestamp asc`

## Configuration
Copy `src/InvoiceProcessing.Functions/local.settings.sample.json` to `local.settings.json`.

| Setting | Meaning |
|---|---|
| `Invoice__OpenAIEndpoint` | Foundry / Azure OpenAI endpoint |
| `Invoice__ModelDeployment` | **Deployment name** of your model in Foundry (the code uses the deployment name, not the model name) |
| `Invoice__DocumentIntelligenceEndpoint` | Document Intelligence endpoint |
| `Invoice__TableName` | Result table (default `Invoices`) |
| `InvoiceStorage` | Connection string (local/Azurite) **or** identity-based: `InvoiceStorage__blobServiceUri`, `InvoiceStorage__queueServiceUri`, `InvoiceStorage__tableServiceUri` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Application Insights |

Auth is `DefaultAzureCredential` (managed identity in Azure). Grant the function's identity:
`Cognitive Services OpenAI User` (Foundry), `Cognitive Services User` (Document Intelligence), `Storage Blob Data Owner`, `Storage Queue Data Contributor`, `Storage Table Data Contributor` (storage).

## Deploy with azd
Infrastructure is in `infra/` (Bicep, subscription scope) and wired up through `azure.yaml`.

```bash
azd auth login
azd up          # provisions + deploys; prompts for env name, region, modelName and modelVersion
```
- **modelName / modelVersion**: the model to deploy in Foundry, exactly as listed in the Foundry model catalog for your region. The deployment name defaults to the model name; override with `azd env set` + `modelDeploymentName` in `infra/main.parameters.json` if you prefer another name. If the model isn't offered in your primary region, set `aiLocation`.
- **Resources**: Log Analytics + Application Insights, one storage account (`invoices` container, `Invoices` table, host storage), one Foundry `AIServices` account (model deployment + Document Intelligence, Entra-only), a Linux App Service plan (B1) and the function app. The function app's system-assigned identity gets all required roles; so do you (blob + table access), so you can upload and inspect.
- **Why not Flex Consumption?** It only supports Event Grid blob triggers (needs an event subscription and a function key after deploy). The polling trigger used here needs a dedicated/Premium plan with Always On; change `functionPlanSku` (e.g. `EP1`) if you want more scale.
- Try it: `az storage blob upload --auth-mode login --account-name $(azd env get-value AZURE_STORAGE_ACCOUNT) -c invoices -f invoice.pdf` then watch Application Insights. The polling trigger can take a few minutes to notice the blob.
- Clean up: `azd down --purge`.

## Run locally
```bash
azurite &                                # blob/queue/table emulator
cd src/InvoiceProcessing.Functions && func start
# upload a PDF/image into the "invoices" container (create it first)
dotnet test                              # reducer tests
```
