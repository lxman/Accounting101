namespace Accounting101.Settlement;

/// <summary>The atom that reduces a document's open balance: an amount applied to one target document
/// (an invoice or a bill), regardless of the funding document.</summary>
public sealed record Allocation(Guid TargetId, decimal Amount);
