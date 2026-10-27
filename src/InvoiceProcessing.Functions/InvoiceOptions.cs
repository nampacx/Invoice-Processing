namespace InvoiceProcessing;

/// <summary>Settings bound from the "Invoice" configuration section (env vars: Invoice__Xxx).</summary>
public sealed class InvoiceOptions
{
    public const string SectionName = "Invoice";

    /// <summary>Azure AI Document Intelligence endpoint.</summary>
    public string DocumentIntelligenceEndpoint { get; set; } = "";

    /// <summary>Azure AI Foundry / Azure OpenAI endpoint hosting the model.</summary>
    public string OpenAIEndpoint { get; set; } = "";

    /// <summary>Name of the model deployment in Foundry (e.g. the gpt-6-luna deployment).</summary>
    public string ModelDeployment { get; set; } = "";

    /// <summary>Table that receives the results.</summary>
    public string TableName { get; set; } = "Invoices";
}
