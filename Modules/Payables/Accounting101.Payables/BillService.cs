using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>The bill lifecycle: draft a bill (plain, editable, discardable scratch), enter it (promote to a new
/// evidentiary id — assigns the number — then post its A/P entry, PendingApproval), and void it (reverse the
/// entry if posted, or withdraw it if still pending). The module never self-approves.</summary>
public sealed class BillService(
    IBillStore bills, IVendorStore vendors, IBillAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<Bill> DraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await ValidateBodyAsync(clientId, body, ct);
        return await bills.CreateDraftAsync(clientId, body, ct);
    }

    /// <summary>Enter a draft: finalize (assigns the number) on a NEW evidentiary id, delete the draft,
    /// then post its A/P entry (PendingApproval). Returns the entered bill — its id differs from the draft's.</summary>
    public async Task<Bill> EnterAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        Bill draft = await RequireAsync(clientId, billId, ct);
        if (draft.Status != BillStatus.Draft)
            throw new InvalidOperationException($"Only a draft bill can be entered; {billId} is {draft.Status}.");
        if (draft.Total <= 0m)
            throw new InvalidOperationException($"Bill {billId} must total more than zero.");

        Bill entered = await bills.PromoteDraftAsync(clientId, billId, ct);
        BillPostingAccounts posting = await accounts.GetBillAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBill(entered, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return entered;
    }

    /// <summary>Edit a draft bill: re-validate and replace the draft body. Throws if the id is not a draft.</summary>
    public async Task<Bill> EditDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await ValidateBodyAsync(clientId, body, ct);
        return await bills.UpdateDraftAsync(clientId, billId, body, ct);
    }

    /// <summary>Discard a draft bill (hard delete, no audit trace). Throws if the id is not a draft —
    /// use <see cref="VoidAsync"/> to cancel an entered bill instead.</summary>
    public Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        bills.DiscardDraftAsync(clientId, billId, ct);

    private async Task ValidateBodyAsync(Guid clientId, BillBody body, CancellationToken ct)
    {
        if (await vendors.GetAsync(clientId, body.VendorId, ct) is null)
            throw new InvalidOperationException($"Vendor {body.VendorId} does not exist.");
        if (body.Lines.Count == 0)
            throw new InvalidOperationException("A bill needs at least one line.");
        if (body.Lines.Any(l => l.Amount <= 0m))
            throw new InvalidOperationException("Every bill line amount must be greater than zero.");
        if (body.Lines.Any(l => l.ExpenseAccountId == Guid.Empty))
            throw new InvalidOperationException("Every bill line needs an expense account.");
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
