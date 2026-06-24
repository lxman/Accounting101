using System.Text.Json.Serialization;

namespace Accounting101.Invoicing;

/// <summary>How far an issued invoice is toward being paid. Derived from applied allocations, never stored.
/// Orthogonal to the Draft/Issued/Void document lifecycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettlementStatus { Open, PartiallyPaid, Paid }
