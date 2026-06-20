namespace Accounting101.Ledger.Mongo;

/// <summary>
/// One subsidiary-ledger line: the signed (debit-positive) balance for a single dimension value within
/// a control account — e.g. what one customer owes inside Accounts Receivable. It is the same journal
/// lines as the trial balance grouped one level finer (account + dimension), so the per-dimension
/// balances sum back to the control-account balance by construction.
/// </summary>
public sealed record SubledgerBalance(Guid AccountId, Guid DimensionValue, decimal Balance);
