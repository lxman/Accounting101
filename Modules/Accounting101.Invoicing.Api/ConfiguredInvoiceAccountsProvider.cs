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
            DefaultRevenueAccountId = Read("Invoicing:Accounts:Revenue"),
            RevenueAccountsByCategory = ReadCategoryMap("Invoicing:Accounts:RevenueByCategory"),
            SalesTaxPayableAccountId = Read("Invoicing:Accounts:SalesTaxPayable"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Invoicing posting account '{key}' is not configured.");

    /// <summary>Bind a category → account-id section. Absent section yields an empty map; a malformed
    /// value fails loud, the same as a required account.</summary>
    private IReadOnlyDictionary<string, Guid> ReadCategoryMap(string sectionKey) =>
        configuration.GetSection(sectionKey).GetChildren().ToDictionary(
            child => child.Key,
            child => Guid.TryParse(child.Value, out Guid id)
                ? id
                : throw new InvalidOperationException(
                    $"Invoicing revenue category '{child.Key}' has a malformed account id '{child.Value}'."));
}
