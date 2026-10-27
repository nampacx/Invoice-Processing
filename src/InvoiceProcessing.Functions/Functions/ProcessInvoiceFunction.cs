using InvoiceProcessing.Agents;
using InvoiceProcessing.Storage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace InvoiceProcessing.Functions;

public sealed class ProcessInvoiceFunction(
    InvoiceAgent agent,
    InvoiceTableWriter tableWriter,
    ILogger<ProcessInvoiceFunction> logger)
{
    [Function(nameof(ProcessInvoice))]
    public async Task ProcessInvoice(
        [BlobTrigger("invoices/{name}", Connection = "InvoiceStorage")] BinaryData blob,
        string name,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        logger.LogInformation("Invoice blob received: {Blob} ({Bytes} bytes)", name, blob.ToMemory().Length);

        try
        {
            var invoice = await agent.ExtractAsync(blob, name, ct);
            await tableWriter.SaveAsync(name, invoice, ct);
            logger.LogInformation("Invoice {Blob} processed: {InvoiceNumber} from {Vendor}, total {Total} {Currency}",
                name, invoice.Transaction.InvoiceNumber, invoice.Vendor.Name, invoice.Transaction.InvoiceTotal, invoice.Transaction.Currency);
        }
        catch (Exception ex)
        {
            // Rethrow so the blob trigger's retry / poison-blob handling applies.
            logger.LogError(ex, "Invoice {Blob} failed", name);
            throw;
        }
    }
}
