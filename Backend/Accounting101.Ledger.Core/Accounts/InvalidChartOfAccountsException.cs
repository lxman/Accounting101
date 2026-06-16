namespace Accounting101.Ledger.Core.Accounts;

/// <summary>Thrown when a set of accounts violates a chart-of-accounts structural invariant.</summary>
public sealed class InvalidChartOfAccountsException(string message) : Exception(message);
