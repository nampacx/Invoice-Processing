# Invoice Processing, with as few Azure resources as possible

Drop an invoice PDF into a storage container. A minute or so later, the vendor, the totals and every line item are sitting in a table. No servers to babysit, no database to design, no PhD in OCR required.

This repo is a small demo of how to build that with **Azure Functions**, **Microsoft Foundry hosted agents** and the **Microsoft Agent Framework**, using the least amount of Azure possible. If you can read a bit of C# and have an Azure subscription, you can run it.

## The idea in 30 seconds

```
 invoice.pdf ──► Blob Storage ──► Function ──"process invoice.pdf"──► Hosted agent (on Foundry)
                                  (only a trigger)                      │
                                                                        ├─ get_invoice_file         ◄── Blob Storage
                                                                        ├─ read_invoice_document    ◄── Document Intelligence
                                                                        └─ save_invoice             ──► Table Storage
```

1. You upload a file to the `invoices` container.
2. Storage fires a "blob created" event, Event Grid hands it to the function's blob trigger, and the function wakes up. The function does exactly one thing: it tells the agent *"process this blob"*.
3. The **agent** (a language model with a job description and three tools) does everything else: it loads the file, reads it, and saves the result.

The function never touches the invoice. It doesn't even download it.

## How it works (the interesting bits)

### The agent has three tools
| Tool | What it does |
|---|---|
| `get_invoice_file` | Downloads the invoice from the `invoices` container. Tells the model only the name, size and type; the file itself stays in the agent's memory. |
| `read_invoice_document` | Runs **Document Intelligence** (`prebuilt-invoice`) on that file, then a **boring, deterministic clean-up** that throws away page coordinates, word polygons and bounding boxes. Document Intelligence returns a *lot* of JSON, and 95% of it is "where on the page is this letter". What's left is plain field values, plus a confidence score only where the OCR wasn't sure. |
| `save_invoice` | Takes the extracted transaction, vendor and line items, checks them, and writes them to the table. |

Why bother with the clean-up? A smaller input means a cheaper, faster and less distracted model. And because the clean-up is plain code, it has unit tests and never has a creative day.

Why does `get_invoice_file` not just hand the file to the model? Because then the model would read a whole PDF as tokens. Keeping the bytes in plain code means the model only ever sees the tidy JSON.

### The model does the thinking
The model works out the parts that aren't a simple field lookup: who actually *issued* the invoice (not who it's billed to), what the line items are, whether the totals add up. If something looks off (low OCR confidence, totals that don't match), it adds a warning instead of making something up.

### "Trust, but verify" for a language model
Models sometimes announce "all done!" without having done it. So the agent host checks the receipts: a run only counts as successful if `save_invoice` actually returned `SAVED`. Otherwise the run fails, the function sees the failure, throws, and the blob trigger retries the file. The model cannot talk its way out of it.

### Results land in one table
One partition per uploaded file: a row called `Invoice` (transaction and vendor) and rows `Line-0001`, `Line-0002`, … for the items. Upload the same file again and it overwrites the old result.

### You can watch the agent think
Application Insights gets the blob arriving, every tool call (with how long it took), how much JSON the clean-up removed, the tool trail of each run, and failures. Prompts and invoice contents are *not* logged, because invoices are private. Handy query to get started:

```
traces | where message has "Agent" or message has "tool" | order by timestamp asc
```

## The Azure bill of materials

| Resource | What it does |
|---|---|
| **Storage account** | Upload container, result table, and the function's own housekeeping. One account, three jobs. |
| **Foundry resource + project** | Hosts the model deployment, Document Intelligence, **and the agent itself**. |
| **Function app** (+ plan) | The trigger. |
| **Event Grid system topic** | Carries the storage account's "blob created" events to the function. |
| **Application Insights** (+ Log Analytics) | The "what on earth is the agent doing" window. |

That's it. No Key Vault, no database, no Service Bus, and no container registry: the agent is deployed from source and Foundry builds it for you.

## Identities: everything is scoped to the agent

There are no keys or secrets anywhere, and nothing uses the shared project identity. Every deployed agent gets **its own** Microsoft Entra identity, and that is the identity that touches your data:

| Who | Can do | Scope |
|---|---|---|
| **The agent's own identity** | Read blobs | the `invoices` container only |
| | Write rows | the `Invoices` table only |
| | Use Document Intelligence and call the model | the Foundry account |
| **The function's identity** | Call this one agent | the agent itself (not the whole project) |
| | Run the blob trigger, send telemetry | storage / Application Insights |

The catch: an agent's identity only exists *after* the agent is deployed, so Bicep can't grant it anything up front. The grants are done by a small `azd` post-deploy hook (`hooks/grant-agent-access.sh`, with a PowerShell twin). It looks up the agent's identity, grants the roles above, and is happy to run again.

The function has a post-deploy hook of its own (`hooks/subscribe-blob-events.sh`, same twin arrangement). It subscribes the function's webhook to the `BlobCreated` events of the `invoices` container. Event Grid insists on shaking hands with that webhook first, and the webhook only exists once the function code is deployed, so this can't be Bicep either.

## Where Foundry fits in

- **The model** lives in Foundry as a deployment. Pick whichever chat model you like from the catalog; the code only needs the *deployment name*. The agent calls it through the **Responses API** on the account's OpenAI v1 endpoint (`https://<account>.openai.azure.com/openai/v1/`) with the plain `OpenAI` client and an Entra token. Reasoning models reject function tools on chat completions, and the plain client keeps the `OpenAI` package version in line with the one the Agent Framework brings along.
- **Document Intelligence** comes along for free on the same Foundry resource. We call it directly with its SDK from inside the agent's tool.
- **The agent** is built with the **Microsoft Agent Framework** and runs as a **Foundry hosted agent**: Foundry runs it for you, gives it an endpoint and an identity, and scales it. It talks the Responses protocol, which is why the function can call it with a plain "process this blob" message.
- **Source deploy**: `azd deploy` zips the agent project, Foundry builds it. No Docker on your machine, no registry in your subscription.

## Run it in Azure

You need the [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) (with the Foundry extensions), the Azure CLI, and a subscription where you can create role assignments (Owner on the resource group is the easy way).

```bash
azd ext install microsoft.foundry   # hosted-agent support (preview)
azd auth login
az login                            # used by the post-deploy hook
azd up
```

`azd` asks for an environment name, a region, and the **model name and version** to deploy. Use exactly what the Foundry model catalog shows for your region (I can't guess your model's name, and neither can a template). Hosted agents are only available in some regions, so check the list if `azd` complains. Then it provisions everything, deploys the function and the agent, and runs the two hooks (Event Grid subscription, agent role grants). Go make a coffee.

Changed only code? `azd deploy` is enough. Changed `infra/`? Use `azd up` (or `azd provision` first), otherwise the hooks find resources that don't exist yet.

Try it:

```bash
az storage blob upload --auth-mode login \
  --account-name $(azd env get-value AZURE_STORAGE_ACCOUNT) \
  -c invoices -f my-invoice.pdf
```

Then look at the `Invoices` table in the portal (or Storage Explorer). Clean up with `azd down --purge`.

## When it doesn't work: where to look

All of these have actually happened. Application Insights shows both sides: the function under its own role name, the hosted agent as `unknown_service:dotnet`.

- **The function fails with `HTTP 424 session_not_ready`.** The function is fine; the agent never got ready (its `/readiness` returns 503). Look at the agent's exceptions in Application Insights. Ours was a `MissingMethodException` in `OpenAI.Responses.ResponsesClient`: `Azure.AI.OpenAI` (beta) was built against an older `OpenAI` package than the one the Agent Framework pulls in. Hence the plain `OpenAIClient` in `InvoiceAgentFactory.cs`. Mixing these preview packages is the usual suspect.
- **Uploads don't trigger anything.** Check that the Event Grid subscription exists and points at the function:
  ```bash
  az eventgrid system-topic event-subscription list -o table \
    -g $(azd env get-value AZURE_RESOURCE_GROUP) \
    --system-topic-name $(azd env get-value AZURE_EVENTGRID_SYSTEM_TOPIC)
  ```
  If it's missing, run `hooks/subscribe-blob-events.sh` (or the `.ps1`) again once the function is up. Files uploaded before the subscription existed never get an event: upload them again.
- **The agent hook** reads the agent's identity from `instance_identity.principal_id` of the agent's REST object. If it can't, check `azd ai agent show` and grant the five roles listed in `hooks/grant-agent-access.sh` by hand.
- **Fresh role assignments** can take a few minutes. A permissions error on the very first invoice usually sorts itself out on the retry.
- **`azure.ai.project` service**: `azure.yaml` points `azd` at the project that `infra/` created (`endpoint: ${AZURE_AI_PROJECT_ENDPOINT}`). If `azd` tries to create its own project instead, that's the line to look at.
- **Agent telemetry** goes to the project's Application Insights connection (created by `infra/`) and needs key ingestion enabled on that resource. The function sends its own telemetry with its managed identity.

## Run the tests

```bash
dotnet test
```
Covers the clean-up step, the tool guards (path tricks, wrong call order) and the "must have saved" check, using a fake model. For the real thing you need the Foundry resource, so run `azd up` and try the upload above.

## Where to look in the code

| File | What's in it |
|---|---|
| `src/InvoiceProcessing.Functions/Functions/ProcessInvoiceFunction.cs` | The blob trigger (Event Grid source). About 20 lines. |
| `src/InvoiceProcessing.Agent/InvoiceAgentFactory.cs` | The agent: instructions, model client, and the "must have saved" check. |
| `src/InvoiceProcessing.Agent/Tools/InvoiceTools.cs` | The three tools, with call logging. |
| `src/InvoiceProcessing.Agent/Tools/InvoiceJsonReducer.cs` | The deterministic clean-up. |
| `src/InvoiceProcessing.Agent/Models/InvoiceExtraction.cs` | The shape of the agent's answer. |
| `src/InvoiceProcessing.Agent/Storage/InvoiceTableWriter.cs` | Writes the table rows. |
| `infra/` | Bicep for the Azure resources. |
| `azure.yaml`, `hooks/` | `azd` wiring: three services (function, project, agent) and the post-deploy hooks (role grants, Event Grid subscription). |

## Limitations (a.k.a. things this demo is honest about)

- **Only new uploads count.** The trigger reacts to "blob created" events, so files that were already in the container before the subscription existed are ignored. Upload them again if you want them processed.
- **It costs money while it sleeps.** The function runs on an always-on plan (B1 by default), so it is billed 24/7 like a lamp someone forgot to switch off. The Event Grid trigger would also run on scale-to-zero Flex Consumption, but that's a change for another day. The hosted agent itself only bills while it works. Run `azd down` when you're done playing.
- **The agent wakes up slowly.** Hosted agents scale to zero when idle, so the first invoice after a quiet spell pays a cold start. It's not hot coffee; it's more "the barista just arrived".
- **Permissions take their sweet time.** Fresh role assignments can need a few minutes to kick in. If the very first invoice fails with a permissions error, make a tea and let the retry do its thing.
- **The agent's identity is a late bloomer.** It exists only after the agent is deployed, which is why a script, not Bicep, hands out its permissions. Yes, that is a bit awkward. No, there isn't a neater way yet.
- **Preview software.** The `azd` Foundry extensions and the hosting packages are still in preview. Things may move. Pin versions before showing this to anyone with a pager.
- **One file, one invoice.** A PDF containing twelve invoices will be treated as one very confusing invoice.
- **No human in the loop.** The agent flags doubts as warnings in the table, but nobody is asked to approve anything. Don't wire this straight into payments. Your accountant will find out.
- **Models are confident, not infallible.** Totals are cross-checked, but a blurry fax of a coffee-stained receipt will still get you a coffee-stained result.
- **Failures are blunt.** If processing fails, the blob trigger retries a few times and then parks the file in a poison queue. There's no dashboard, no alert and no apology.
- **It's a table, not a database.** Table Storage is cheap and simple, and just as happy to refuse a fancy query.
- **Not production-hardened.** Public endpoints, no private networking, and the model itself is, shall we say, non-deterministic. It's a demo, not a bank.
