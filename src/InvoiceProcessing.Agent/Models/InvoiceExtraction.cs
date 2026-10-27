using System.ComponentModel;

namespace InvoiceProcessing.Models;

/// <summary>The extracted invoice. Its shape is the JSON schema of the <c>invoice</c> argument of save_invoice.</summary>
public sealed class InvoiceExtraction
{
    [Description("Invoice-level transaction data.")]
    public TransactionInfo Transaction { get; set; } = new();

    [Description("The party that issued the invoice (the vendor / seller).")]
    public VendorInfo Vendor { get; set; } = new();

    [Description("All line items on the invoice, in document order.")]
    public List<LineItem> LineItems { get; set; } = [];

    [Description("Short notes about missing, ambiguous or inconsistent data. Empty if none.")]
    public List<string> Warnings { get; set; } = [];
}

public sealed class TransactionInfo
{
    [Description("Invoice number / ID as printed on the document.")]
    public string? InvoiceNumber { get; set; }

    [Description("Invoice date in ISO format yyyy-MM-dd.")]
    public string? InvoiceDate { get; set; }

    [Description("Due date in ISO format yyyy-MM-dd.")]
    public string? DueDate { get; set; }

    [Description("Purchase order number, if present.")]
    public string? PurchaseOrder { get; set; }

    [Description("ISO 4217 currency code, e.g. EUR or USD.")]
    public string? Currency { get; set; }

    public decimal? SubTotal { get; set; }
    public decimal? TotalTax { get; set; }

    [Description("Gross amount due / invoice total.")]
    public decimal? InvoiceTotal { get; set; }

    [Description("Payment terms, e.g. 'Net 30'.")]
    public string? PaymentTerms { get; set; }
}

public sealed class VendorInfo
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? TaxId { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

public sealed class LineItem
{
    public string? Description { get; set; }
    public string? ProductCode { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? TaxAmount { get; set; }

    [Description("Line total for this item.")]
    public decimal? Amount { get; set; }
}
