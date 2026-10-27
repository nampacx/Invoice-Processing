using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using InvoiceProcessing;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

// One credential for every Azure client: the function's managed identity when running in Azure.
TokenCredential credential = AzureCredentials.Create();
builder.Services.AddSingleton(credential);

builder.Services.AddOptions<InvoiceOptions>()
    .Bind(builder.Configuration.GetSection(InvoiceOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.FoundryProjectEndpoint) && !string.IsNullOrWhiteSpace(o.AgentName),
        "Invoice:FoundryProjectEndpoint and Invoice:AgentName must be set.")
    .ValidateOnStart();

// Application Insights: traces/metrics/logs through OpenTelemetry (APPLICATIONINSIGHTS_CONNECTION_STRING).
builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    // Entra-authenticated ingestion (the function app's identity has Monitoring Metrics Publisher).
    builder.Services.AddOpenTelemetry().UseAzureMonitorExporter(o => o.Credential = credential);
}
builder.Logging.AddFilter("InvoiceProcessing", LogLevel.Information);

// Client bound to the hosted agent's own endpoint. One call covers a whole agent run (possibly a cold
// start, Document Intelligence and several model turns), so the default 100 s network timeout is too short.
#pragma warning disable OPENAI001
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<InvoiceOptions>>().Value;
    return new AIProjectClient(new Uri(o.FoundryProjectEndpoint), credential)
        .ProjectOpenAIClient
        .GetProjectResponsesClientForAgentEndpoint(o.AgentName, options: new ProjectOpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(10) });
});

builder.Build().Run();
