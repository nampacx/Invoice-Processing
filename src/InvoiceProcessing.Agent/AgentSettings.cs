namespace InvoiceProcessing;

/// <summary>Settings for the hosted agent, read from environment variables (set in azure.yaml / by the platform).</summary>
public sealed record AgentSettings(
    Uri OpenAIEndpoint,
    string ModelDeployment,
    Uri BlobEndpoint,
    Uri TableEndpoint,
    Uri DocumentIntelligenceEndpoint,
    string InvoiceContainer,
    string InvoiceTable)
{
    public static AgentSettings FromEnvironment() => new(
        OpenAIEndpoint: new Uri(Required("AZURE_OPENAI_ENDPOINT")),
        ModelDeployment: Required("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
        BlobEndpoint: new Uri(Required("STORAGE_BLOB_ENDPOINT")),
        TableEndpoint: new Uri(Required("STORAGE_TABLE_ENDPOINT")),
        DocumentIntelligenceEndpoint: new Uri(Required("DOCUMENT_INTELLIGENCE_ENDPOINT")),
        InvoiceContainer: Environment.GetEnvironmentVariable("INVOICE_CONTAINER") is { Length: > 0 } c ? c : "invoices",
        InvoiceTable: Environment.GetEnvironmentVariable("INVOICE_TABLE") is { Length: > 0 } t ? t : "Invoices");

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Environment variable {name} is not set.");
}
