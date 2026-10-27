using Azure;
using Azure.Data.Tables;
using InvoiceProcessing.Models;
using Microsoft.Extensions.Logging;

namespace InvoiceProcessing.Storage;

/// <summary>
/// Writes one invoice into a single table. All rows of an invoice share the partition key (the source
/// blob name), so header and line items are written in one atomic batch. Re-processing the same blob
/// overwrites the previous result (upsert).
/// <list type="bullet">
/// <item>RowKey "Invoice": transaction + vendor data</item>
/// <item>RowKey "Line-0001", "Line-0002", ...: line items</item>
/// </list>
/// </summary>
public sealed class InvoiceTableWriter(TableClient table, ILogger<InvoiceTableWriter> logger)
{
    private const int MaxBatch = 100;

    public async Task SaveAsync(string blobName, InvoiceExtraction invoice, CancellationToken ct)
    {
        await table.CreateIfNotExistsAsync(ct);

        var partitionKey = ToKey(blobName);
        var t = invoice.Transaction;
        var v = invoice.Vendor;

        var rows = new List<TableEntity>
        {
            new(partitionKey, "Invoice")
            {
                ["SourceBlob"] = blobName,
                ["ProcessedAt"] = DateTimeOffset.UtcNow,
                ["InvoiceNumber"] = t.InvoiceNumber,
                ["InvoiceDate"] = t.InvoiceDate,
                ["DueDate"] = t.DueDate,
                ["PurchaseOrder"] = t.PurchaseOrder,
                ["Currency"] = t.Currency,
                ["SubTotal"] = t.SubTotal is { } st ? (double)st : null,
                ["TotalTax"] = t.TotalTax is { } tt ? (double)tt : null,
                ["InvoiceTotal"] = t.InvoiceTotal is { } it ? (double)it : null,
                ["PaymentTerms"] = t.PaymentTerms,
                ["VendorName"] = v.Name,
                ["VendorAddress"] = v.Address,
                ["VendorTaxId"] = v.TaxId,
                ["VendorEmail"] = v.Email,
                ["VendorPhone"] = v.Phone,
                ["LineItemCount"] = invoice.LineItems.Count,
                ["Warnings"] = string.Join(" | ", invoice.Warnings),
            },
        };

        for (var i = 0; i < invoice.LineItems.Count; i++)
        {
            var l = invoice.LineItems[i];
            rows.Add(new(partitionKey, $"Line-{i + 1:D4}")
            {
                ["Description"] = l.Description,
                ["ProductCode"] = l.ProductCode,
                ["Quantity"] = l.Quantity is { } q ? (double)q : null,
                ["Unit"] = l.Unit,
                ["UnitPrice"] = l.UnitPrice is { } up ? (double)up : null,
                ["TaxAmount"] = l.TaxAmount is { } ta ? (double)ta : null,
                ["Amount"] = l.Amount is { } a ? (double)a : null,
            });
        }

        foreach (var chunk in rows.Chunk(MaxBatch))
        {
            var actions = chunk.Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e));
            await table.SubmitTransactionAsync(actions, ct);
        }

        logger.LogInformation("Saved invoice {Blob} to table {Table}: 1 header + {Lines} line rows (partition {Partition})",
            blobName, table.Name, invoice.LineItems.Count, partitionKey);
    }

    /// <summary>Table keys must not contain / \ # ? or control characters.</summary>
    private static string ToKey(string blobName) =>
        new(blobName.Select(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c) ? '_' : c).ToArray());
}
