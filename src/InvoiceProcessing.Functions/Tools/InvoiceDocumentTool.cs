using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace InvoiceProcessing.Tools;

/// <summary>
/// The single tool the agent uses to read an invoice: Document Intelligence (prebuilt-invoice)
/// followed by the deterministic <see cref="InvoiceJsonReducer"/>. One instance is bound to one
/// uploaded document, so the model never has to pass file contents through a tool argument.
/// </summary>
public sealed class InvoiceDocumentTool(
    DocumentIntelligenceClient client,
    ILogger<InvoiceDocumentTool> logger)
{
    public const string Name = "read_invoice_document";

    /// <summary>Creates the <see cref="AITool"/> for one document, with call logging.</summary>
    public AITool CreateFor(BinaryData document, string documentName)
    {
        string? cached = null;

        // Cached so a model retry does not pay for a second Document Intelligence call.
        async Task<string> ReadInvoiceDocument(CancellationToken ct) =>
            cached ??= await AnalyzeAsync(document, documentName, ct);

        var function = AIFunctionFactory.Create(
            ReadInvoiceDocument,
            name: Name,
            description: "Reads the uploaded invoice with Azure Document Intelligence and returns the " +
                         "cleaned-up extracted fields as JSON (vendor, customer, dates, totals, line items " +
                         "under 'Items'). Takes no arguments; always operates on the current invoice. " +
                         "Fields with low OCR confidence are returned as { value, confidence }.");

        return new LoggingAIFunction(function, logger);
    }

    private async Task<string> AnalyzeAsync(BinaryData document, string documentName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("Document Intelligence: analyzing {Document} ({Bytes} bytes) with prebuilt-invoice",
            documentName, document.ToMemory().Length);

        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-invoice", document, cancellationToken: ct);
        var raw = JsonNode.Parse(ModelReaderWriter.Write(operation.Value, ModelReaderWriterOptions.Json))!;
        var rawLength = raw.ToJsonString().Length;

        var reduced = InvoiceJsonReducer.Reduce(raw).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        logger.LogInformation(
            "Document Intelligence finished in {ElapsedMs} ms: raw JSON {RawChars} chars -> reduced {ReducedChars} chars",
            sw.ElapsedMilliseconds, rawLength, reduced.Length);
        return reduced;
    }

    /// <summary>Logs every invocation of the wrapped tool: arguments, duration, result size and failures.</summary>
    private sealed class LoggingAIFunction(AIFunction inner, ILogger logger) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            logger.LogInformation("Agent tool call -> {Tool}({Arguments})", Name,
                JsonSerializer.Serialize(arguments.ToDictionary(a => a.Key, a => a.Value)));
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await base.InvokeCoreAsync(arguments, cancellationToken);
                logger.LogInformation("Agent tool result <- {Tool} succeeded in {ElapsedMs} ms ({Chars} chars)",
                    Name, sw.ElapsedMilliseconds, (result as JsonElement?)?.GetRawText().Length ?? result?.ToString()?.Length ?? 0);
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
