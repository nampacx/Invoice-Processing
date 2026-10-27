namespace InvoiceProcessing;

/// <summary>Settings bound from the "Invoice" configuration section (env vars: Invoice__Xxx).</summary>
public sealed class InvoiceOptions
{
    public const string SectionName = "Invoice";

    /// <summary>Endpoint of the Foundry project that hosts the agent (https://&lt;account&gt;.services.ai.azure.com/api/projects/&lt;project&gt;).</summary>
    public string FoundryProjectEndpoint { get; set; } = "";

    /// <summary>Name of the hosted agent to call.</summary>
    public string AgentName { get; set; } = "invoice-extractor";
}
