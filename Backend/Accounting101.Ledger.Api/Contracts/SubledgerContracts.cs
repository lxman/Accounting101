namespace Accounting101.Ledger.Api.Contracts;

/// <summary>
/// A subsidiary ledger: balances within a control account broken out by one dimension (customer,
/// vendor, or item). The lines sum to the control-account balance on the trial balance.
/// </summary>
public sealed record SubledgerResponse(
    string Dimension,
    DateOnly? AsOf,
    IReadOnlyList<SubledgerLineResponse> Lines);

/// <summary>One dimension value's balance (debit-positive), e.g. one customer's A/R.</summary>
public sealed record SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance);

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
