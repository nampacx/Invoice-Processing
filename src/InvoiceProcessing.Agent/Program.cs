using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using InvoiceProcessing;
using InvoiceProcessing.Storage;
using InvoiceProcessing.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var settings = AgentSettings.FromEnvironment();

// Inside the hosted container this is the agent's own identity (token service); locally your az login.
TokenCredential credential = AzureCredentials.Create();

// Telemetry: Foundry injects the project's Application Insights connection string into the container.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource(InvoiceAgentFactory.TelemetrySource))
        .UseAzureMonitorExporter();
}
builder.Logging.AddFilter("InvoiceProcessing", LogLevel.Information);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(credential);
builder.Services.AddSingleton(sp => new BlobServiceClient(settings.BlobEndpoint, credential).GetBlobContainerClient(settings.InvoiceContainer));
builder.Services.AddSingleton(sp => new DocumentIntelligenceClient(settings.DocumentIntelligenceEndpoint, credential));
builder.Services.AddSingleton(sp => new TableServiceClient(settings.TableEndpoint, credential).GetTableClient(settings.InvoiceTable));
builder.Services.AddSingleton<InvoiceTableWriter>();
builder.Services.AddSingleton<InvoiceFileCache>();
builder.Services.AddSingleton<InvoiceTools>();
builder.Services.AddSingleton<AIAgent>(sp => InvoiceAgentFactory.Create(
    settings, credential, sp.GetRequiredService<InvoiceTools>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("InvoiceProcessing.Agent")));

// Expose the agent through the Foundry Responses protocol (/responses on port 8088 in the hosted container).
builder.Services.AddFoundryResponses();

var app = builder.Build();
app.MapFoundryResponses();
app.Run();
