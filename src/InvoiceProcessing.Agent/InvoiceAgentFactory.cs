using System.Runtime.CompilerServices;
using System.ClientModel.Primitives;
using OpenAI;
using Azure.Core;
using InvoiceProcessing.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace InvoiceProcessing;

/// <summary>Builds the invoice extraction agent that Foundry hosts.</summary>
public static class InvoiceAgentFactory
{
    public const string AgentName = "invoice-extractor";
    public const string TelemetrySource = "InvoiceProcessing.Agent";

    private const string Instructions = """
        You are an invoice data extraction agent. You receive the name of an invoice blob and must process it
        with your tools, in this order:

        1. get_invoice_file(blobName)        - load the invoice.
        2. read_invoice_document(blobName)   - read it; you get cleaned-up JSON.
        3. save_invoice(blobName, invoice)   - save the extracted data. Call it exactly once, last.

        From the JSON of step 2 extract: the transaction data (invoice number, dates, currency, totals, PO,
        payment terms), the vendor (who sent / issued the invoice) and every line item.

        Rules:
        - Use the blob name exactly as given in the request.
        - Use only information in the tool result. Never invent values; use null when something is missing.
        - The vendor is the party that ISSUED the invoice, not the customer / bill-to party.
        - Dates must be ISO yyyy-MM-dd. Amounts are plain decimal numbers without currency symbols.
        - A field delivered as { value, confidence } had low OCR confidence; add a short entry to `warnings`
          for any such field you rely on.
        - If totals do not match the sum of the line items, add a warning describing the difference.
        - If a tool returns text starting with "ERROR:", fix the problem and retry; if you cannot, stop and
          say what went wrong. Never claim the invoice was saved unless save_invoice returned "SAVED".
        - When done, reply with one short sentence.
        """;

    public static AIAgent Create(AgentSettings settings, TokenCredential credential, InvoiceTools tools, ILogger logger)
    {
        // The model is called with the agent's own identity (not the project's): the account's OpenAI
        // endpoint, role "Cognitive Services OpenAI User" granted to this agent by the postdeploy hook.
        // Responses API (not chat completions): reasoning models reject function tools combined with reasoning_effort there.
#pragma warning disable OPENAI001
        // Reasoning models can think for longer than the default 100 s network timeout.
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.OpenAIEndpoint, "openai/v1/"),
            NetworkTimeout = TimeSpan.FromMinutes(5),
        };
        var tokenPolicy = new BearerTokenPolicy(credential, "https://cognitiveservices.azure.com/.default");
        IChatClient chatClient = new OpenAIClient(tokenPolicy, options)
            .GetResponsesClient()
            .AsIChatClient(settings.ModelDeployment);
#pragma warning restore OPENAI001

        AIAgent agent = new ChatClientAgent(chatClient, instructions: Instructions, name: AgentName, tools: tools.CreateTools());

        return Decorate(agent, logger);
    }

    /// <summary>Adds run logging, the "must have saved" check and telemetry around an agent.</summary>
    public static AIAgent Decorate(AIAgent agent, ILogger logger)
    {
        return agent
            .AsBuilder()
            .Use(
                runFunc: (messages, session, options, inner, ct) => RunAsync(messages, session, options, inner, logger, ct),
                runStreamingFunc: (messages, session, options, inner, ct) => RunStreamingAsync(messages, session, options, inner, logger, ct))
            .UseOpenTelemetry(TelemetrySource)
            .Build();
    }

    // Safety net: a run only counts as successful if save_invoice actually returned SAVED. Otherwise the
    // run fails, the caller sees an error and the blob trigger retries - the model cannot talk its way out of it.
    private static async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("Agent run started");
        var response = await inner.RunAsync(messages, session, options, ct);
        LogToolTrail(response.Messages.SelectMany(m => m.Contents), logger);
        EnsureSaved(response.Messages.SelectMany(m => m.Contents), logger);
        logger.LogInformation("Agent run finished: {Summary}", response.Text);
        return response;
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner, ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation("Agent run started (streaming)");
        var contents = new List<AIContent>();
        await foreach (var update in inner.RunStreamingAsync(messages, session, options, ct))
        {
            contents.AddRange(update.Contents);
            yield return update;
        }
        LogToolTrail(contents, logger);
        EnsureSaved(contents, logger);
        logger.LogInformation("Agent run finished (streaming)");
    }

    private static void EnsureSaved(IEnumerable<AIContent> contents, ILogger logger)
    {
        var list = contents.ToList();
        var saveCallIds = list.OfType<FunctionCallContent>()
            .Where(c => c.Name == InvoiceTools.SaveTool).Select(c => c.CallId).ToHashSet();

        var saved = list.OfType<FunctionResultContent>()
            .Any(r => saveCallIds.Contains(r.CallId) && r.Exception is null
                      && r.Result?.ToString()?.TrimStart('"').StartsWith(InvoiceTools.SavedMarker, StringComparison.Ordinal) == true);

        if (!saved)
        {
            logger.LogError("Agent finished without a successful {Tool} call", InvoiceTools.SaveTool);
            throw new InvalidOperationException($"The agent finished without a successful {InvoiceTools.SaveTool} call; the invoice was not saved.");
        }
    }

    private static void LogToolTrail(IEnumerable<AIContent> contents, ILogger logger)
    {
        foreach (var content in contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    logger.LogInformation("Model requested tool {Tool} (callId {CallId})", call.Name, call.CallId);
                    break;
                case FunctionResultContent result:
                    logger.LogInformation("Tool result delivered to model (callId {CallId}, exception: {Exception})",
                        result.CallId, result.Exception?.Message ?? "none");
                    break;
            }
        }
    }
}
