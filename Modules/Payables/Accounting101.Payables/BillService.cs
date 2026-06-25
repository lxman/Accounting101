using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>The bill lifecycle: draft a bill, enter it (finalize — assigns the number — then post its A/P
/// entry, which lands PendingApproval for a separate approver), and void it (reverse the entry if posted,
/// or withdraw it if still pending). The module never self-approves.</summary>
public sealed class BillService(
    IBillStore bills, IVendorStore vendors, IBillAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<Bill> DraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await vendors.GetAsync(clientId, body.VendorId, ct) is null)
            throw new InvalidOperationException($"Vendor {body.VendorId} does not exist.");
        if (body.Lines.Count == 0)
            throw new InvalidOperationException("A bill needs at least one line.");
        if (body.Lines.Any(l => l.Amount <= 0m))
            throw new InvalidOperationException("Every bill line amount must be greater than zero.");
        if (body.Lines.Any(l => l.ExpenseAccountId == Guid.Empty))
            throw new InvalidOperationException("Every bill line needs an expense account.");
        return await bills.CreateDraftAsync(clientId, body, ct);
    }

    public async Task<Bill> EnterAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        Bill draft = await RequireAsync(clientId, billId, ct);
        if (draft.Status != BillStatus.Draft)
            throw new InvalidOperationException($"Only a draft bill can be entered; {billId} is {draft.Status}.");
        if (draft.Total <= 0m)
            throw new InvalidOperationException($"Bill {billId} must total more than zero.");

        Bill entered = await bills.FinalizeAsync(clientId, billId, ct);
        BillPostingAccounts posting = await accounts.GetBillAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBill(entered, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return entered;
    }

    public async Task<Bill> VoidAsync(Guid clientId, Guid billId, string? reason = null, CancellationToken ct = default)
    {
        Bill bill = await RequireAsync(clientId, billId, ct);
        if (bill.Status != BillStatus.Entered)
            throw new InvalidOperationException($"Only an entered bill can be voided; {billId} is {bill.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, billId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for bill {bill.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(bill.BillDate, reason ?? $"Voided bill {billId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided bill {billId}"), ct);

        await bills.VoidAsync(clientId, billId, ct);
        return await RequireAsync(clientId, billId, ct);
    }

    public Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        bills.GetAsync(clientId, billId, ct);

    private async Task<Bill> RequireAsync(Guid clientId, Guid billId, CancellationToken ct) =>
        await bills.GetAsync(clientId, billId, ct) ?? throw new InvalidOperationException($"Bill {billId} not found.");
}
