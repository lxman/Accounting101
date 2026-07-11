namespace Accounting101.Ledger.Contracts;

/// <summary>Post many journal entries as one atomic business event (e.g. a payroll run). All-or-nothing:
/// every entry validates and writes, or none do. Max 500 entries.</summary>
public sealed record PostBatchRequest(IReadOnlyList<PostEntryRequest> Entries);
