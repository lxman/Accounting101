using System.Text.Json.Serialization;

namespace Accounting101.Settlement;

/// <summary>How far a document is toward being settled. Derived from applied allocations, never stored;
/// orthogonal to a document's own lifecycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettlementStatus { Open, PartiallyPaid, Paid }
