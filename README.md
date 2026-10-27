# Invoice Processing, with as few Azure resources as possible

Drop an invoice PDF into a storage container. A minute or so later, the vendor, the totals and every line item are sitting in a table. No servers to babysit, no database to design, no PhD in OCR required.

This repo is a small demo of how to build that with **Azure Functions**, **Microsoft Foundry** and the **Microsoft Agent Framework**, using the least amount of Azure possible. If you can read a bit of C# and have an Azure subscription, you can run it.

## The idea in 30 seconds

```
  invoice.pdf ──► Blob Storage ──► Azure Function ──► Agent ──► Table Storage
                                                        │
                                                        └─ uses one tool: "read the invoice"
                                                             (Document Intelligence + a tidy-up step)
```

1. You upload a file to the `invoices` container.
2. A blob trigger wakes up the function.
3. The function hands the file to an **agent** (a language model with a job description and a tool).
4. The agent reads the invoice using its tool, then answers with structured data: *who sent it, what the transaction is, what was bought*.
5. The answer is written to a table. Done.

## How it works (the interesting bits)

### The agent has exactly one tool
The tool is called `read_invoice_document`. Under the hood it does two things:

1. **Document Intelligence** (the `prebuilt-invoice` model) does the heavy lifting: OCR, finding the fields, spotting the line-item table.
2. **A boring, deterministic clean-up** throws away everything the model doesn't need: page coordinates, word polygons, bounding boxes. Document Intelligence returns a *lot* of JSON, and 95% of it is "where on the page is this letter". The clean-up reduces it to plain field values, and only keeps a confidence score when the OCR wasn't sure.

Why bother? A smaller input means a cheaper, faster and less distracted model. And because the clean-up is plain code, it has unit tests and never has a creative day.

The tool is created per invoice and already knows which file it's working on. The model just says "read the invoice", so the file contents never travel through the prompt.

### The agent does the thinking
The model gets the tidy JSON and has to work out the parts that aren't a simple field lookup: who actually *issued* the invoice (not who it's billed to), what the line items are, whether the totals add up. It answers in a fixed shape (`InvoiceExtraction`), so there's no parsing of free text. If something looks off (low OCR confidence, totals that don't match), it adds a warning instead of making something up.

### Results land in one table
One partition per uploaded file: a row called `Invoice` (transaction and vendor) and rows `Line-0001`, `Line-0002`, … for the items. Upload the same file again and it overwrites the old result.

### You can watch the agent think
Everything goes to Application Insights: the blob arriving, every tool call (with how long it took), how much JSON the clean-up removed, token usage, warnings, failures. The model calls and tool calls also show up as traces. Handy query to get started:

```
traces | where message has "Agent" | order by timestamp asc
```

Prompts and invoice contents are *not* recorded by default, because invoices are private. There's a one-line switch in `InvoiceAgent.cs` if you want them for debugging.

## The Azure bill of materials

| Resource | What it does |
|---|---|
| **Storage account** | Upload container, result table, and the function's own housekeeping. One account, three jobs. |
| **Foundry resource** (+ project) | Hosts the model deployment *and* Document Intelligence. One resource, two services. |
| **Function app** (+ plan) | Runs the code. |
| **Application Insights** (+ Log Analytics) | The "what on earth is the agent doing" window. |

That's it. No Key Vault, no database, no Service Bus, no container registry.

There are also **no keys or secrets** anywhere. The function app uses its own managed identity to talk to storage, Foundry and Application Insights, and the deployment template turns off key-based access on all of them. Nothing to rotate, nothing to leak. (Locally it uses your `az login`.)

## Where Foundry fits in

- **The model** lives in Foundry as a deployment. Pick whichever chat model you like from the catalog; the code only needs the *deployment name*.
- **Document Intelligence** comes along for free on the same Foundry resource. It's a "Foundry tool" in the sense that it's one of the AI services the resource provides, and we call it directly with its SDK.
- **The Foundry project** is created so you have a home in the Foundry portal to look at the deployment, try the model in the playground, and grow from there.
- **The agent itself** is built with the **Microsoft Agent Framework** and runs inside the function. We are not using Foundry's hosted Agent Service. Our agent is a few lines of C# and a tool, and a function app is plenty of hosting for that.

## Run it in Azure

You need the [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) and a subscription.

```bash
azd auth login
azd up
```

`azd` asks for an environment name, a region, and the **model name and version** to deploy. Use exactly what the Foundry model catalog shows for your region (I can't guess your model's name, and neither can a template). Then it provisions everything and deploys the function. Go make a coffee.

Try it:

```bash
az storage blob upload --auth-mode login \
  --account-name $(azd env get-value AZURE_STORAGE_ACCOUNT) \
  -c invoices -f my-invoice.pdf
```

Then look at the `Invoices` table in the portal (or Storage Explorer). Clean up with `azd down --purge`.

Your user gets blob and table access from the template, so the upload above works without extra setup.

## Run it locally

You need the .NET 10 SDK, [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) and [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite). Foundry and Document Intelligence still live in Azure, so deploy those first (`azd provision` is enough).

```bash
cp src/InvoiceProcessing.Functions/local.settings.sample.json src/InvoiceProcessing.Functions/local.settings.json
# fill in the three Invoice__ values from `azd env get-values`
azurite &
cd src/InvoiceProcessing.Functions && func start
```

Create an `invoices` container in Azurite, drop a file in, and watch the console. Unit tests for the clean-up step: `dotnet test`.

## Where to look in the code

| File | What's in it |
|---|---|
| `Functions/ProcessInvoiceFunction.cs` | The blob trigger. Short. |
| `Agents/InvoiceAgent.cs` | The agent: instructions, model client, telemetry. |
| `Tools/InvoiceDocumentTool.cs` | The tool: Document Intelligence + tool-call logging. |
| `Tools/InvoiceJsonReducer.cs` | The deterministic clean-up. |
| `Models/InvoiceExtraction.cs` | The shape of the agent's answer. |
| `Storage/InvoiceTableWriter.cs` | Writes the table rows. |
| `infra/` | Bicep for everything above. `azure.yaml` makes it an `azd` project. |

## Limitations (a.k.a. things this demo is honest about)

- **It's not instant.** We use the classic polling blob trigger, which checks the container every so often. "Real time" it is not; a new invoice can sit there for a few minutes, politely waiting its turn. The faster Event Grid trigger needs extra plumbing and doesn't fit a one-command deploy. Trade-off accepted.
- **It costs money while it sleeps.** Polling needs an always-on plan (B1 by default), so the function app is billed 24/7 like a lamp someone forgot to switch off. Scale-to-zero plans can't poll blobs. Run `azd down` when you're done playing.
- **One file, one invoice.** A PDF containing twelve invoices will be treated as one very confusing invoice.
- **No human in the loop.** The agent flags doubts as warnings in the table, but nobody is asked to approve anything. Don't wire this straight into payments. Your accountant will find out.
- **Models are confident, not infallible.** Totals are cross-checked, but a blurry fax of a coffee-stained receipt will still get you a coffee-stained result.
- **Failures are blunt.** If processing fails, the blob trigger retries a few times and then parks the file in a poison queue. There's no dashboard, no alert and no apology.
- **It's a table, not a database.** Table Storage is cheap and simple, and just as happy to refuse a fancy query.
- **Not production-hardened.** Public endpoints, no private networking, no tests for the agent itself (the model is, shall we say, non-deterministic). It's a demo, not a bank.
