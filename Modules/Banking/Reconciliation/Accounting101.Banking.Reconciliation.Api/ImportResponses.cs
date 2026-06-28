namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>The parse-to-preview result of importing a bank export: one or more parsed statements (a file
/// may carry several) plus non-fatal warnings. Nothing is created — the client reviews, supplies any missing
/// balances, and submits to POST /bank-statements.</summary>
public sealed record ImportPreviewResponse(IReadOnlyList<StatementPreview> Statements, IReadOnlyList<string> Warnings);

public sealed record StatementPreview(
    IReadOnlyList<BankStatementLineRequest> Lines,
    decimal? DetectedOpeningBalance, decimal? DetectedClosingBalance,
    DateOnly? StatementDate, string? AccountHint);
