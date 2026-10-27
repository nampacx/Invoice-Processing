using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using InvoiceProcessing.Models;
using InvoiceProcessing.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace InvoiceProcessing.Tools;

/// <summary>
/// The three tools of the invoice agent. All access to Azure happens here, with the agent's own identity:
/// <list type="number">
/// <item><c>get_invoice_file</c>: download the blob (no public or signed URLs involved)</item>
/// <item><c>read_invoice_document</c>: Document Intelligence + deterministic clean-up</item>
/// <item><c>save_invoice</c>: write the extraction to Table Storage</item>
/// </list>
/// Validation problems are returned as "ERROR: ..." text so the model can correct itself; real failures throw.
/// </summary>
public sealed class InvoiceTools(
    BlobContainerClient container,
    DocumentIntelligenceClient documentIntelligence,
    InvoiceTableWriter tableWriter,
    InvoiceFileCache cache,
    ILogger<InvoiceTools> logger)
{
    public const string GetFileTool = "get_invoice_file";
    public const string ReadDocumentTool = "read_invoice_document";
    public const string SaveTool = "save_invoice";

    /// <summary>Prefix of the text <c>save_invoice</c> returns on success; checked by the agent host.</summary>
    public const string SavedMarker = "SAVED";

    private const long MaxFileBytes = 20 * 1024 * 1024;

    public IList<AITool> CreateTools() =>
    [
        Logged(AIFunctionFactory.Create(GetInvoiceFileAsync, name: GetFileTool,
            description: "Loads an uploaded invoice from the invoices container into memory. Returns its name, size and content type. " +
                         "Call this first. The file content itself is never returned; use read_invoice_document to read it.")),
        Logged(AIFunctionFactory.Create(ReadInvoiceDocumentAsync, name: ReadDocumentTool,
            description: "Reads a loaded invoice with Azure Document Intelligence and returns the cleaned-up extracted fields as JSON " +
                         "(vendor, customer, dates, totals, line items under 'Items'). Fields with low OCR confidence are returned as " +
                         "{ value, confidence }. The invoice must have been loaded with get_invoice_file.")),
        Logged(AIFunctionFactory.Create(SaveInvoiceAsync, name: SaveTool,
            description: "Saves the extracted invoice (transaction, vendor, line items, warnings) to the results table. " +
                         "Call this exactly once, as the last step, after the data has been extracted.")),
    ];

    private async Task<string> GetInvoiceFileAsync(
        [Description("Name of the blob in the invoices container, exactly as given in the request.")] string blobName,
        CancellationToken ct)
    {
        if (!IsValidBlobName(blobName)) return $"ERROR: '{blobName}' is not a valid blob name.";

        var blob = container.GetBlobClient(blobName);
        var properties = await TryGetPropertiesAsync(blob, ct);
        if (properties is null) return $"ERROR: blob '{blobName}' was not found in the '{container.Name}' container.";
        if (properties.ContentLength > MaxFileBytes)
            return $"ERROR: blob '{blobName}' is {properties.ContentLength} bytes; the limit is {MaxFileBytes}.";

        var download = await blob.DownloadContentAsync(ct);
        cache.Put(blobName, download.Value.Content, properties.ContentType);

        logger.LogInformation("Loaded {Blob} from {Container}: {Bytes} bytes, {ContentType}",
            blobName, container.Name, properties.ContentLength, properties.ContentType);

        return JsonSerializer.Serialize(new { blobName, sizeBytes = properties.ContentLength, contentType = properties.ContentType });
    }

    private async Task<string> ReadInvoiceDocumentAsync(
        [Description("Name of the blob that was loaded with get_invoice_file.")] string blobName,
        CancellationToken ct)
    {
        var file = cache.Get(blobName);
        if (file is null) return $"ERROR: '{blobName}' is not loaded. Call {GetFileTool} first.";
        if (file.ReducedJson is { } cached) return cached;

        var sw = Stopwatch.StartNew();
        logger.LogInformation("Document Intelligence: analyzing {Blob} with prebuilt-invoice", blobName);

        var operation = await documentIntelligence.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-invoice", file.Content, cancellationToken: ct);
        var rawJson = ModelReaderWriter.Write(operation.Value, ModelReaderWriterOptions.Json);
        var raw = JsonNode.Parse(rawJson)!;

        var reduced = InvoiceJsonReducer.Reduce(raw).ToJsonString();
        file.ReducedJson = reduced;

        logger.LogInformation(
            "Document Intelligence finished in {ElapsedMs} ms: raw JSON {RawBytes} bytes -> reduced {ReducedChars} chars",
            sw.ElapsedMilliseconds, rawJson.ToMemory().Length, reduced.Length);
        return reduced;
    }

    private async Task<string> SaveInvoiceAsync(
        [Description("Name of the blob that was loaded and read.")] string blobName,
        [Description("The extracted invoice data.")] InvoiceExtraction invoice,
        CancellationToken ct)
    {
        if (cache.Get(blobName) is null) return $"ERROR: '{blobName}' is not loaded. Call {GetFileTool} and {ReadDocumentTool} first.";
        if (invoice is null) return "ERROR: invoice data is missing.";

        await tableWriter.SaveAsync(blobName, invoice, ct);
        cache.Remove(blobName);

        return $"{SavedMarker}: {blobName} ({invoice.LineItems.Count} line items)";
    }

    private static bool IsValidBlobName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 1024
        && !name.StartsWith('/')
        && !name.Contains('\\')
        && !name.Split('/').Any(part => part is ".." or "." or "")
        && !name.Any(char.IsControl);

    private static async Task<Azure.Storage.Blobs.Models.BlobProperties?> TryGetPropertiesAsync(BlobClient blob, CancellationToken ct)
    {
        try { return (await blob.GetPropertiesAsync(cancellationToken: ct)).Value; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    private AITool Logged(AIFunction function) => new LoggingAIFunction(function, logger);

    /// <summary>Logs every tool invocation: name, arguments (blob name only), duration and outcome.</summary>
    private sealed class LoggingAIFunction(AIFunction inner, ILogger logger) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Only the blob name is logged: invoice data stays out of the logs.
            arguments.TryGetValue("blobName", out var blobName);
            logger.LogInformation("Agent tool call -> {Tool}(blobName: {Blob})", Name, blobName);

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await base.InvokeCoreAsync(arguments, cancellationToken);
                var text = (result as JsonElement?)?.ToString() ?? result?.ToString() ?? "";
                if (text.StartsWith("ERROR:", StringComparison.Ordinal))
                    logger.LogWarning("Agent tool result <- {Tool} returned an error after {ElapsedMs} ms: {Error}", Name, sw.ElapsedMilliseconds, text);
                else
                    logger.LogInformation("Agent tool result <- {Tool} succeeded in {ElapsedMs} ms ({Chars} chars)", Name, sw.ElapsedMilliseconds, text.Length);
                return result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent tool {Tool} failed after {ElapsedMs} ms", Name, sw.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
