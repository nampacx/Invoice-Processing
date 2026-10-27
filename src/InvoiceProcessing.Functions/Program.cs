using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Monitor.OpenTelemetry.Exporter;
using InvoiceProcessing;
using InvoiceProcessing.Agents;
using InvoiceProcessing.Storage;
using InvoiceProcessing.Tools;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = FunctionsApplication.CreateBuilder(args);

// One credential for every Azure client: the function's managed identity when running in Azure.
TokenCredential credential = AzureCredentials.Create();
builder.Services.AddSingleton(credential);

builder.Services.AddOptions<InvoiceOptions>()
    .Bind(builder.Configuration.GetSection(InvoiceOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.DocumentIntelligenceEndpoint)
                && !string.IsNullOrWhiteSpace(o.OpenAIEndpoint)
                && !string.IsNullOrWhiteSpace(o.ModelDeployment),
        "Invoice:DocumentIntelligenceEndpoint, Invoice:OpenAIEndpoint and Invoice:ModelDeployment must be set.")
    .ValidateOnStart();

// Application Insights: traces/metrics/logs through OpenTelemetry (APPLICATIONINSIGHTS_CONNECTION_STRING).
// The agent + model spans (gen_ai.*, execute_tool) are emitted from the "InvoiceProcessing.Agent" source.
builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .WithTracing(t => t.AddSource(InvoiceAgent.TelemetrySource));
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    // Entra-authenticated ingestion (the Application Insights resource has local auth disabled).
    builder.Services.AddOpenTelemetry().UseAzureMonitorExporter(o => o.Credential = credential);
}

// Logs from our namespace at Information so tool calls and agent runs show up in Application Insights.
builder.Logging.AddFilter("InvoiceProcessing", LogLevel.Information);

builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<InvoiceOptions>>().Value;
    return new DocumentIntelligenceClient(new Uri(o.DocumentIntelligenceEndpoint), credential);
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<InvoiceOptions>>().Value;
    var config = sp.GetRequiredService<IConfiguration>();
    // Identity-based: InvoiceStorage__tableServiceUri, falling back to a connection string for local dev (Azurite).
    var service = config["InvoiceStorage:tableServiceUri"] is { Length: > 0 } uri
        ? new TableServiceClient(new Uri(uri), credential)
        : new TableServiceClient(config["InvoiceStorage"] ?? "UseDevelopmentStorage=true");
    return service.GetTableClient(o.TableName);
});
builder.Services.AddSingleton<InvoiceDocumentTool>();
builder.Services.AddSingleton<InvoiceAgent>();
builder.Services.AddSingleton<InvoiceTableWriter>();

builder.Build().Run();
