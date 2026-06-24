using System.Text.Json.Serialization;

namespace Accounting101.Invoicing;

/// <summary>
/// Where an invoice sits in its own lifecycle. Draft has no ledger effect; issuing it posts the
/// receivable entry; voiding it reverses that entry. (Paid arrives with cash application — deferred.)
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InvoiceStatus
{
    Draft,
    Issued,
    Void,
}
