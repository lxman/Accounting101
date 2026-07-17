using Accounting101.Ledger.Api.Control;

namespace Accounting101.Payables.Api;

/// <summary>Resolves the three payables posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Payables:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen. The <c>Payable</c> slot is shared:
/// the same resolved account flows into both the bill and payment recipes.</summary>
public sealed class StoreBackedBillAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IBillAccountsProvider
{
    public async Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "payables", ct);
        return new BillPostingAccounts { PayableAccountId = Resolve(stored, "Payable") };
    }

    public async Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "payables", ct);
        return new BillPaymentPostingAccounts
        {
            PayableAccountId       = Resolve(stored, "Payable"),
            CashAccountId          = Resolve(stored, "Cash"),
            VendorCreditsAccountId = Resolve(stored, "VendorCredits"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Payables:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Payables posting account 'Payables:Accounts:{slot}' is not configured.");
}
