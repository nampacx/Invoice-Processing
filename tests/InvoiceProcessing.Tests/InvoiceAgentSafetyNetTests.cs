using InvoiceProcessing.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceProcessing.Tests;

public class InvoiceAgentSafetyNetTests
{
    private static AIAgent AgentWith(IChatClient model, string saveResult = "SAVED: a.pdf (2 line items)")
    {
        var save = AIFunctionFactory.Create((string blobName) => saveResult, name: InvoiceTools.SaveTool);
        var inner = new ChatClientAgent(model, instructions: "test", tools: [save]);
        return InvoiceAgentFactory.Decorate(inner, NullLogger.Instance);
    }

    [Fact]
    public async Task Run_succeeds_when_save_invoice_returned_SAVED()
    {
        var agent = AgentWith(new ScriptedModel(callSave: true));

        var response = await agent.RunAsync("Process the invoice blob 'a.pdf'.");

        Assert.Contains("done", response.Text);
    }

    [Fact]
    public async Task Run_fails_when_the_model_claims_success_without_saving()
    {
        var agent = AgentWith(new ScriptedModel(callSave: false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("Process the invoice blob 'a.pdf'."));

        Assert.Contains(InvoiceTools.SaveTool, ex.Message);
    }

    [Fact]
    public async Task Run_fails_when_save_invoice_returned_an_error()
    {
        var agent = AgentWith(new ScriptedModel(callSave: true), saveResult: "ERROR: 'a.pdf' is not loaded.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("Process the invoice blob 'a.pdf'."));
    }

    [Fact]
    public async Task Streaming_run_fails_when_nothing_was_saved()
    {
        var agent = AgentWith(new ScriptedModel(callSave: false));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("Process the invoice blob 'a.pdf'.")) { }
        });
    }

    /// <summary>A fake model: optionally asks for the save_invoice tool once, then says "done".</summary>
    private sealed class ScriptedModel(bool callSave) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var alreadyCalled = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            var reply = callSave && !alreadyCalled
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", InvoiceTools.SaveTool, new Dictionary<string, object?> { ["blobName"] = "a.pdf" })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await GetResponseAsync(messages, options, ct);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
