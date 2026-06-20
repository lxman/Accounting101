using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Invoicing.Mongo;

/// <summary>
/// Mongo storage shape for an <see cref="Invoice"/>. Dates as ISO <c>yyyy-MM-dd</c> strings (sort
/// chronologically), money as Decimal128, status as a string. Totals/tax are derived on the domain
/// record, never stored.
/// </summary>
public sealed class InvoiceDocument
{
    private const string DateFormat = "yyyy-MM-dd";

    [BsonId]
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }
    public string Number { get; set; } = string.Empty;
    public string IssueDate { get; set; } = string.Empty;
    public string? DueDate { get; set; }

    [BsonRepresentation(BsonType.String)]
    public InvoiceStatus Status { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal TaxRate { get; set; }

    public string? Memo { get; set; }
    public List<InvoiceLineDocument> Lines { get; set; } = [];

    public static InvoiceDocument FromDomain(Invoice i) => new()
    {
        Id = i.Id,
        CustomerId = i.CustomerId,
        Number = i.Number,
        IssueDate = i.IssueDate.ToString(DateFormat, CultureInfo.InvariantCulture),
        DueDate = i.DueDate?.ToString(DateFormat, CultureInfo.InvariantCulture),
        Status = i.Status,
        TaxRate = i.TaxRate,
        Memo = i.Memo,
        Lines = i.Lines.Select(InvoiceLineDocument.FromDomain).ToList(),
    };

    public Invoice ToDomain() => new()
    {
        Id = Id,
        CustomerId = CustomerId,
        Number = Number,
        IssueDate = DateOnly.ParseExact(IssueDate, DateFormat, CultureInfo.InvariantCulture),
        DueDate = DueDate is null ? null : DateOnly.ParseExact(DueDate, DateFormat, CultureInfo.InvariantCulture),
        Status = Status,
        TaxRate = TaxRate,
        Memo = Memo,
        Lines = Lines.Select(l => l.ToDomain()).ToList(),
    };
}

/// <summary>Mongo storage shape for one <see cref="InvoiceLine"/>. Amount is derived, never stored.</summary>
public sealed class InvoiceLineDocument
{
    public string Description { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Quantity { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal UnitPrice { get; set; }

    public bool Taxable { get; set; }

    public static InvoiceLineDocument FromDomain(InvoiceLine l) => new()
    {
        Description = l.Description,
        Quantity = l.Quantity,
        UnitPrice = l.UnitPrice,
        Taxable = l.Taxable,
    };

    public InvoiceLine ToDomain() => new()
    {
        Description = Description,
        Quantity = Quantity,
        UnitPrice = UnitPrice,
        Taxable = Taxable,
    };
}
