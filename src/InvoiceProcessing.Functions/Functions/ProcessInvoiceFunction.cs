using Azure.AI.Extensions.OpenAI;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;

namespace InvoiceProcessing.Functions;

/// <summary>
/// Does one thing: when an invoice lands in the container, ask the hosted agent to process it.
/// The agent loads the file, reads it, and saves the result itself, using its own identity.
/// </summary>
#pragma warning disable OPENAI001
public sealed class ProcessInvoiceFunction(ProjectResponsesClient agent, ILogger<ProcessInvoiceFunction> logger)
{
    [Function(nameof(ProcessInvoice))]
    public async Task ProcessInvoice(
        // BlobClient = just a reference; the function never downloads the invoice.
        [BlobTrigger("invoices/{name}", Connection = "InvoiceStorage")] BlobClient blob,
        string name,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        logger.LogInformation("Invoice blob received: {Blob}. Asking the hosted agent to process it", name);

        ResponseResult response = await agent.CreateResponseAsync($"Process the invoice blob '{name}'.", cancellationToken: ct);

        // The agent host fails the response when save_invoice did not succeed.
        // Throwing here lets the blob trigger retry the file and eventually move it to the poison queue.
        if (response.Status != ResponseStatus.Completed)
        {
            var reason = response.Error?.Message ?? response.Status?.ToString() ?? "unknown";
            logger.LogError("Hosted agent did not complete for {Blob}: {Status} - {Reason}", name, response.Status, reason);
            throw new InvalidOperationException($"Hosted agent failed for '{name}': {reason}");
        }

        foreach (var item in response.OutputItems.OfType<FunctionCallResponseItem>())
            logger.LogInformation("Agent called tool {Tool} for {Blob}", item.FunctionName, name);

        logger.LogInformation("Invoice {Blob} processed by the hosted agent: {Summary}", name, response.GetOutputText());
    }
}
