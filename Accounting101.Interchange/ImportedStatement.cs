namespace Accounting101.Interchange;

/// <summary>A parsed bank statement — the neutral shape importers produce (NOT the Reconciliation domain's
/// BankStatement). Balances/date are populated only when the source format carries them (OFX does; CSV
/// usually doesn't).</summary>
public sealed record ImportedStatement(
    IReadOnlyList<ImportedLine> Lines, decimal? OpeningBalance, decimal? ClosingBalance,
    DateOnly? StatementDate, string? AccountHint);

/// <summary>One parsed statement line. Amount is signed from the bank's perspective (+ in, − out).</summary>
public sealed record ImportedLine(DateOnly Date, decimal Amount, string Description, string? Reference);
