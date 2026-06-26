namespace Accounting101.Receivables.Api;

/// <summary>Supplies the chart accounts the payment recipes post to, from configuration
/// (Receivables:Accounts:Receivable|Cash|CustomerCredits). A single configured set for now.</summary>
public sealed class ConfiguredPaymentAccountsProvider(IConfiguration configuration) : IPaymentAccountsProvider
{
    public Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new PaymentPostingAccounts
        {
            ReceivableAccountId = Read("Receivables:Accounts:Receivable"),
            CashAccountId = Read("Receivables:Accounts:Cash"),
            CustomerCreditsAccountId = Read("Receivables:Accounts:CustomerCredits"),
            BadDebtExpenseAccountId = Read("Receivables:Accounts:BadDebtExpense"),
            SalesReturnsAccountId = Read("Receivables:Accounts:SalesReturns"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Receivables posting account '{key}' is not configured.");
}
