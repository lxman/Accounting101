using Accounting101.Invoicing;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Invoicing.Api;

/// <summary>Supplies the chart accounts the payment recipes post to, from configuration
/// (Invoicing:Accounts:Receivable|Cash|CustomerCredits). A single configured set for now.</summary>
public sealed class ConfiguredPaymentAccountsProvider(IConfiguration configuration) : IPaymentAccountsProvider
{
    public Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new PaymentPostingAccounts
        {
            ReceivableAccountId = Read("Invoicing:Accounts:Receivable"),
            CashAccountId = Read("Invoicing:Accounts:Cash"),
            CustomerCreditsAccountId = Read("Invoicing:Accounts:CustomerCredits"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Invoicing posting account '{key}' is not configured.");
}
