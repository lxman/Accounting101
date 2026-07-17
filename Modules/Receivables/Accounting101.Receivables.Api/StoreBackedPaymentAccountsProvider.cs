using Accounting101.Ledger.Api.Control;

namespace Accounting101.Receivables.Api;

/// <summary>Resolves the five payment posting accounts per client: each is the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Receivables:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen. The <c>Receivable</c> slot is shared
/// with the invoice provider (both resolve the same slot).</summary>
public sealed class StoreBackedPaymentAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IPaymentAccountsProvider
{
    public async Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "receivables", ct);
        return new PaymentPostingAccounts
        {
            ReceivableAccountId      = Resolve(stored, "Receivable"),
            CashAccountId            = Resolve(stored, "Cash"),
            CustomerCreditsAccountId = Resolve(stored, "CustomerCredits"),
            BadDebtExpenseAccountId  = Resolve(stored, "BadDebtExpense"),
            SalesReturnsAccountId    = Resolve(stored, "SalesReturns"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Receivables:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Receivables posting account 'Receivables:Accounts:{slot}' is not configured.");
}
