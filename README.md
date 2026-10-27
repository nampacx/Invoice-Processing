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

## Run locally
```bash
azurite &                                # blob/queue/table emulator
cd src/InvoiceProcessing.Functions && func start
# upload a PDF/image into the "invoices" container (create it first)
dotnet test                              # reducer tests
```
