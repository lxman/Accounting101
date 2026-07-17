namespace Accounting101.Ledger.Contracts;

/// <summary>
/// A subsidiary ledger: balances within a control account broken out by one dimension (customer,
/// vendor, or item). The lines sum to the control-account balance on the trial balance.
/// </summary>
public sealed record SubledgerResponse(
    string Dimension,
    DateOnly? AsOf,
    IReadOnlyList<SubledgerLineResponse> Lines);

/// <summary>One dimension value's balance (debit-positive), e.g. one customer's A/R.</summary>
public sealed record SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance, string? Number = null, string? Name = null);

/// <summary>
/// Whether a control account's subsidiary ledger ties to its general-ledger balance. The subledger sees
/// only lines that carry the dimension; <see cref="Variance"/> is the untagged remainder (control minus
/// subledger). A non-zero variance is a control exception — lines hit the account without the dimension.
/// </summary>
public sealed record SubledgerReconciliationResponse(
    Guid Account,
    string Dimension,
    DateOnly? AsOf,
    decimal ControlBalance,
    decimal SubledgerTotal,
    decimal Variance,
    bool TiesOut);

/// <summary>Every dimensioned control account tied out against its GL balance, one line per (account,
/// required dimension). Chart-driven: an account is a control account iff it has RequiredDimensions.</summary>
public sealed record SubledgerReconciliationsResponse(
    DateOnly? AsOf,
    IReadOnlyList<SubledgerReconciliationLine> Lines);

/// <summary>One control account reconciled on one dimension. Variance is the untagged remainder
/// (ControlBalance − SubledgerTotal); TiesOut is Variance == 0.</summary>
public sealed record SubledgerReconciliationLine(
    Guid Account, string? Number, string? Name, string Dimension,
    decimal ControlBalance, decimal SubledgerTotal, decimal Variance, bool TiesOut);
