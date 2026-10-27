using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Azure.AI.DocumentIntelligence;
using InvoiceProcessing.Tools;
using Xunit;

namespace InvoiceProcessing.Tests;

public class InvoiceJsonReducerTests
{
    // Shaped like a real prebuilt-invoice response, including the geometry the reducer must drop.
    private const string Sample = """
    {
      "apiVersion": "2024-11-30", "modelId": "prebuilt-invoice", "stringIndexType": "textElements",
      "content": "INVOICE ...",
      "pages": [{ "pageNumber": 1, "angle": 0, "width": 8.5, "height": 11, "unit": "inch",
                  "words": [{ "content": "INVOICE", "polygon": [1,1,2,1,2,2,1,2], "confidence": 0.99, "span": {"offset":0,"length":7} }],
                  "spans": [{"offset":0,"length":10}] }],
      "documents": [{
        "docType": "invoice", "confidence": 0.97,
        "boundingRegions": [{ "pageNumber": 1, "polygon": [0,0,8.5,0,8.5,11,0,11] }],
        "spans": [{"offset":0,"length":10}],
        "fields": {
          "VendorName": { "type": "string", "valueString": "Contoso Ltd.", "content": "Contoso Ltd.", "confidence": 0.98,
                          "boundingRegions": [{ "pageNumber": 1, "polygon": [1,1,2,1,2,2,1,2] }], "spans": [{"offset":1,"length":12}] },
          "VendorAddress": { "type": "address", "content": "1 Main St\nSeattle WA", "confidence": 0.9,
                             "valueAddress": { "city": "Seattle", "streetAddress": "1 Main St" } },
          "InvoiceDate": { "type": "date", "valueDate": "2025-03-01", "content": "3/1/2025", "confidence": 0.5 },
          "InvoiceTotal": { "type": "currency", "valueCurrency": { "amount": 119.0, "currencySymbol": "€", "currencyCode": "EUR" },
                            "content": "€119.00", "confidence": 0.95 },
          "Items": { "type": "array", "confidence": 0.9, "valueArray": [
            { "type": "object", "confidence": 0.9, "valueObject": {
                "Description": { "type": "string", "valueString": "Widget", "confidence": 0.95 },
                "Quantity": { "type": "number", "valueNumber": 2, "confidence": 0.95 },
                "Amount": { "type": "currency", "valueCurrency": { "amount": 100.0, "currencyCode": "EUR" }, "confidence": 0.95 } } } ] }
        }
      }]
    }
    """;

    [Fact]
    public void Reduce_keeps_values_and_drops_geometry()
    {
        // Round-trip through the SDK type so the test uses the exact wire format the tool sees at runtime.
        var result = ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(Sample), ModelReaderWriterOptions.Json)!;
        var raw = JsonNode.Parse(ModelReaderWriter.Write(result, ModelReaderWriterOptions.Json))!;

        var reduced = InvoiceJsonReducer.Reduce(raw);
        var json = reduced.ToJsonString();

        Assert.Equal("Contoso Ltd.", (string?)reduced["fields"]!["VendorName"]);
        Assert.Equal("1 Main St\nSeattle WA", (string?)reduced["fields"]!["VendorAddress"]);
        Assert.Equal(119.0, (double)reduced["fields"]!["InvoiceTotal"]!["amount"]!);
        Assert.Equal("EUR", (string?)reduced["fields"]!["InvoiceTotal"]!["currencyCode"]);

        var line = reduced["fields"]!["Items"]![0]!;
        Assert.Equal("Widget", (string?)line["Description"]);
        Assert.Equal(2, (double)line["Quantity"]!);
        Assert.Equal(100.0, (double)line["Amount"]!["amount"]!);

        foreach (var dropped in new[] { "polygon", "boundingRegions", "spans", "words", "pages", "currencySymbol", "streetAddress" })
            Assert.DoesNotContain(dropped, json);
    }

    [Fact]
    public void Reduce_flags_low_confidence_fields_only()
    {
        var reduced = InvoiceJsonReducer.Reduce(JsonNode.Parse(Sample)!);

        var date = reduced["fields"]!["InvoiceDate"]!;
        Assert.Equal("2025-03-01", (string?)date["value"]);
        Assert.Equal(0.5, (double)date["confidence"]!);
        Assert.DoesNotContain("confidence", reduced["fields"]!["VendorName"]!.ToJsonString());
    }

    [Fact]
    public void Reduce_without_documents_returns_empty_fields()
    {
        var reduced = InvoiceJsonReducer.Reduce(JsonNode.Parse("""{ "documents": [] }""")!);
        Assert.Empty(reduced["fields"]!.AsObject());
    }
}
