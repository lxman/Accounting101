using Accounting101.Ledger.Api.Control;

namespace Accounting101.Receivables.Api;

/// <summary>Resolves the invoice posting accounts per client: each fixed account is the one configured on
/// the posting-accounts admin screen if set, else the process config value (<c>Receivables:Accounts:*</c>)
/// — so behavior is unchanged until a per-client account is chosen. The dynamic
/// <c>RevenueAccountsByCategory</c> map is NOT per-client-configurable; it is still read from config
/// (<c>Receivables:Accounts:RevenueByCategory</c>) unchanged.</summary>
public sealed class StoreBackedInvoiceAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IInvoiceAccountsProvider
{
    public async Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "receivables", cancellationToken);
        return new InvoicePostingAccounts
        {
            ReceivableAccountId       = Resolve(stored, "Receivable"),
            DefaultRevenueAccountId   = Resolve(stored, "Revenue"),
            SalesTaxPayableAccountId  = Resolve(stored, "SalesTaxPayable"),
            RevenueAccountsByCategory = ReadCategoryMap("Receivables:Accounts:RevenueByCategory"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Receivables:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Receivables posting account 'Receivables:Accounts:{slot}' is not configured.");

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
