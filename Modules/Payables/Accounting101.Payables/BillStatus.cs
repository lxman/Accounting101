using System.Text.Json.Serialization;

namespace Accounting101.Payables;

/// <summary>Where a bill sits in its own lifecycle. Draft has no ledger effect; entering it posts the
/// A/P entry; voiding it reverses that entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BillStatus { Draft, Entered, Void }
