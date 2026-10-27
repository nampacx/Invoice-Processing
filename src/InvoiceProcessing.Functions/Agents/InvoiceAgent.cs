using Azure.AI.OpenAI;
using Azure.Identity;
using InvoiceProcessing.Models;
using InvoiceProcessing.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceProcessing.Agents;

/// <summary>Builds the invoice extraction agent (Microsoft Agent Framework) and runs it for one document.</summary>
public sealed class InvoiceAgent
{
    public const string TelemetrySource = "InvoiceProcessing.Agent";

    private const string Instructions = """
        You are an invoice data extraction agent.

        Workflow:
        1. Call the `read_invoice_document` tool exactly once to read the current invoice.
        2. From its JSON, extract: the transaction data (invoice number, dates, currency, totals, PO,
           payment terms), the vendor (who sent / issued the invoice) and every line item.

        Rules:
        - Use only information in the tool result. Never invent values; use null when something is missing.
        - The vendor is the party that ISSUED the invoice, not the customer / bill-to party.
        - Dates must be ISO yyyy-MM-dd. Amounts are plain decimal numbers without currency symbols.
        - A field delivered as { value, confidence } had low OCR confidence; add a short entry to
          `warnings` for any such field you rely on.
        - If totals do not match the sum of the line items, add a warning describing the difference.
        """;

    private readonly AIAgent _agent;
    private readonly InvoiceDocumentTool _documentTool;
    private readonly ILogger<InvoiceAgent> _logger;

    public InvoiceAgent(
        IOptions<InvoiceOptions> options,
        InvoiceDocumentTool documentTool,
        ILoggerFactory loggerFactory,
        ILogger<InvoiceAgent> logger)
    {
        _documentTool = documentTool;
        _logger = logger;

        var o = options.Value;
        // Managed identity in Azure, developer credentials locally.
        var chatClient = new AzureOpenAIClient(new Uri(o.OpenAIEndpoint), new DefaultAzureCredential())
            .GetChatClient(o.ModelDeployment)
            .AsIChatClient()
            .AsBuilder()
            .UseLogging(loggerFactory)                            // raw model requests (Debug/Trace level)
            .UseOpenTelemetry(loggerFactory, TelemetrySource)     // gen_ai spans per model call incl. token usage
            .Build();

        // The agent is stateless and shared; the document-bound tool is supplied per run via ChatOptions.
        // ChatClientAgent adds function invocation, which executes the tool calls requested by the model.
        _agent = new ChatClientAgent(chatClient, instructions: Instructions, name: "InvoiceExtractor", loggerFactory: loggerFactory)
            .AsBuilder()
            .UseOpenTelemetry(TelemetrySource)                    // agent-level span: invoke_agent InvoiceExtractor
            .Build();
    }

    public async Task<InvoiceExtraction> ExtractAsync(BinaryData document, string documentName, CancellationToken ct)
    {
        var tool = _documentTool.CreateFor(document, documentName);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = [tool] });

        _logger.LogInformation("Agent run started for {Document}", documentName);
        var response = await _agent.RunAsync<InvoiceExtraction>(
            $"Process the invoice '{documentName}'.", session: null, serializerOptions: null, options: runOptions, cancellationToken: ct);

        LogToolActivity(response, documentName);

        _logger.LogInformation(
            "Agent run finished for {Document}: {Lines} line items, vendor '{Vendor}', {Warnings} warnings, tokens in/out {In}/{Out}",
            documentName, response.Result.LineItems.Count, response.Result.Vendor.Name, response.Result.Warnings.Count,
            response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
        foreach (var warning in response.Result.Warnings)
            _logger.LogWarning("Agent warning for {Document}: {Warning}", documentName, warning);

        return response.Result;
    }

    /// <summary>Writes the agent's tool-call/tool-result trail from the response messages to the log.</summary>
    private void LogToolActivity(AgentResponse response, string documentName)
    {
        foreach (var content in response.Messages.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case FunctionCallContent call:
                    _logger.LogInformation("[{Document}] model requested tool {Tool} (callId {CallId})", documentName, call.Name, call.CallId);
                    break;
                case FunctionResultContent result:
                    _logger.LogInformation("[{Document}] tool result delivered to model (callId {CallId}, exception: {Exception})",
                        documentName, result.CallId, result.Exception?.Message ?? "none");
                    break;
            }
        }
    }
}
