using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Api;

/// <summary>
/// Supplies the chart accounts the invoicing recipe posts to, from configuration
/// (<c>Invoicing:Accounts:Receivable|Revenue|SalesTaxPayable</c>). A single configured set for now;
/// per-client discovery of posting accounts from the chart is deferred.
/// </summary>
public sealed class ConfiguredInvoiceAccountsProvider(IConfiguration configuration) : IInvoiceAccountsProvider
{
    public Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InvoicePostingAccounts
        {
            ReceivableAccountId = Read("Invoicing:Accounts:Receivable"),
            RevenueAccountId = Read("Invoicing:Accounts:Revenue"),
            SalesTaxPayableAccountId = Read("Invoicing:Accounts:SalesTaxPayable"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Invoicing posting account '{key}' is not configured.");
}
