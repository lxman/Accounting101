namespace Accounting101.Banking.Cash;

/// <summary>The configured account the cash recipes post to. Supplied by configuration;
/// no hardcoded account numbers.</summary>
public sealed class CashPostingAccounts
{
    /// <summary>Cash (or bank account) — credited on disbursements, debited on deposits.</summary>
    public required Guid CashAccountId { get; init; }
}
