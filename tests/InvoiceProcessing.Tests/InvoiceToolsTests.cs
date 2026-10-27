using Azure.AI.DocumentIntelligence;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using InvoiceProcessing.Models;
using InvoiceProcessing.Storage;
using InvoiceProcessing.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceProcessing.Tests;

/// <summary>Guards that never reach Azure: bad names and wrong call order are rejected before any client is used.</summary>
public class InvoiceToolsTests
{
    private static InvoiceTools CreateTools() => new(
        new BlobContainerClient(new Uri("https://example.blob.core.windows.net/invoices")),
        new DocumentIntelligenceClient(new Uri("https://example.cognitiveservices.azure.com/"), new Azure.AzureKeyCredential("x")),
        new InvoiceTableWriter(new TableClient(new Uri("https://example.table.core.windows.net/Invoices")), NullLogger<InvoiceTableWriter>.Instance),
        new InvoiceFileCache(),
        NullLogger<InvoiceTools>.Instance);

    private static async Task<string> Call(InvoiceTools tools, string tool, Dictionary<string, object?> args)
    {
        var function = tools.CreateTools().OfType<AIFunction>().Single(f => f.Name == tool);
        var result = await function.InvokeAsync(new AIFunctionArguments(args));
        return result?.ToString() ?? "";
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b.pdf")]
    [InlineData("folder//b.pdf")]
    [InlineData("")]
    public async Task get_invoice_file_rejects_suspicious_names(string name)
    {
        var result = await Call(CreateTools(), InvoiceTools.GetFileTool, new() { ["blobName"] = name });

        Assert.StartsWith("ERROR:", result);
    }

    [Fact]
    public async Task read_invoice_document_requires_the_file_to_be_loaded_first()
    {
        var result = await Call(CreateTools(), InvoiceTools.ReadDocumentTool, new() { ["blobName"] = "a.pdf" });

        Assert.StartsWith("ERROR:", result);
        Assert.Contains(InvoiceTools.GetFileTool, result);
    }

    [Fact]
    public async Task save_invoice_requires_the_file_to_be_loaded_first()
    {
        var result = await Call(CreateTools(), InvoiceTools.SaveTool, new()
        {
            ["blobName"] = "a.pdf",
            ["invoice"] = new InvoiceExtraction(),
        });

        Assert.StartsWith("ERROR:", result);
    }
}
