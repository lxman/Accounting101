using Accounting101.Payables;

namespace Accounting101.Payables.Api;

/// <summary>Supplies the payables posting accounts from configuration
/// (Payables:Accounts:Payable|Cash|VendorCredits). A single configured set for now.</summary>
public sealed class ConfiguredBillAccountsProvider(IConfiguration configuration) : IBillAccountsProvider
{
    public Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new BillPostingAccounts { PayableAccountId = Read("Payables:Accounts:Payable") });

    public Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new BillPaymentPostingAccounts
        {
            PayableAccountId = Read("Payables:Accounts:Payable"),
            CashAccountId = Read("Payables:Accounts:Cash"),
            VendorCreditsAccountId = Read("Payables:Accounts:VendorCredits"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Payables posting account '{key}' is not configured.");
}
