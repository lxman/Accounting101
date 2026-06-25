using Accounting101.Receivables;

namespace Accounting101.Receivables.Api;

/// <summary>
/// Supplies the chart accounts the receivables recipe posts to, from configuration
/// (<c>Receivables:Accounts:Receivable|Revenue|SalesTaxPayable</c>). A single configured set for now;
/// per-client discovery of posting accounts from the chart is deferred.
/// </summary>
public sealed class ConfiguredInvoiceAccountsProvider(IConfiguration configuration) : IInvoiceAccountsProvider
{
    public Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InvoicePostingAccounts
        {
            ReceivableAccountId = Read("Receivables:Accounts:Receivable"),
            DefaultRevenueAccountId = Read("Receivables:Accounts:Revenue"),
            RevenueAccountsByCategory = ReadCategoryMap("Receivables:Accounts:RevenueByCategory"),
            SalesTaxPayableAccountId = Read("Receivables:Accounts:SalesTaxPayable"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Receivables posting account '{key}' is not configured.");

    /// <summary>Bind a category → account-id section. Absent section yields an empty map; a malformed
    /// value fails loud, the same as a required account.</summary>
    private IReadOnlyDictionary<string, Guid> ReadCategoryMap(string sectionKey) =>
        configuration.GetSection(sectionKey).GetChildren().ToDictionary(
            child => child.Key,
            child => Guid.TryParse(child.Value, out Guid id)
                ? id
                : throw new InvalidOperationException(
                    $"Receivables revenue category '{child.Key}' has a malformed account id '{child.Value}'."));
}
