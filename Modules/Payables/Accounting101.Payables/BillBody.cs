namespace Accounting101.Payables;

/// <summary>One stored bill line (no computed fields), so the body round-trips through the document store.</summary>
public sealed record BillLineBody(string Description, decimal Amount, Guid ExpenseAccountId);

/// <summary>The stored shape of a bill — commercial content only. Number and status derive from the
/// engine's envelope.</summary>
public sealed record BillBody(
    Guid VendorId, DateOnly BillDate, DateOnly? DueDate, string? VendorReference, string? Memo,
    IReadOnlyList<BillLineBody> Lines);
