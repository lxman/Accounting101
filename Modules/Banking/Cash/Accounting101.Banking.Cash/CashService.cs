using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash;

/// <summary>The cash lifecycle: record a disbursement (persist evidentiary doc + post PendingApproval entry)
/// and void it (reverse the entry if posted, or withdraw if still pending). Same pair for deposits.
/// The module never self-approves.</summary>
public sealed class CashService(
    ICashDisbursementStore disbursements,
    ICashDepositStore deposits,
    ICashAccountsProvider accounts,
    ILedgerClient ledger)
{
    // ── Cash Disbursements ───────────────────────────────────────────────────

    public async Task<CashDisbursement> RecordDisbursementAsync(Guid clientId, CashDisbursementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        CashDisbursement disbursement = await disbursements.RecordAsync(clientId, body, ct);
        // 2. Resolve posting accounts and compose the balanced entry via the Task-1 recipe.
        CashPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);
        PostEntryRequest entry = CashPosting.ComposeDisbursement(disbursement.Id, body, postingAccounts);
        // 3. Post PendingApproval — the module never approves its own entries.
        await ledger.PostAsync(clientId, entry, ct);
        return disbursement;
    }

    public async Task<CashDisbursement> VoidDisbursementAsync(Guid clientId, Guid id, string? reason = null, CancellationToken ct = default)
    {
        CashDisbursement disbursement = await RequireDisbursementAsync(clientId, id, ct);
        if (disbursement.Status != CashDisbursementStatus.Posted)
            throw new InvalidOperationException($"Only a posted cash disbursement can be voided; {id} is {disbursement.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, id, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for cash disbursement {disbursement.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(disbursement.Date, reason ?? $"Voided cash disbursement {id}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided cash disbursement {id}"), ct);

        await disbursements.VoidAsync(clientId, id, ct);
        return await RequireDisbursementAsync(clientId, id, ct);
    }

    public async Task<CashDisbursement?> GetDisbursementAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDisbursement? doc = await disbursements.GetAsync(clientId, id, ct);
        if (doc is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, id, ct);
        return doc.Status == CashDisbursementStatus.Void || CashLedgerStatus.ShowsVoided(entries)
            ? doc with { Status = CashDisbursementStatus.Void }
            : doc;
    }

    // ── Cash Deposits ────────────────────────────────────────────────────────

    public async Task<CashDeposit> RecordDepositAsync(Guid clientId, CashDepositBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        CashDeposit deposit = await deposits.RecordAsync(clientId, body, ct);
        // 2. Resolve posting accounts and compose the balanced entry via the Task-1 recipe.
        CashPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);
        PostEntryRequest entry = CashPosting.ComposeDeposit(deposit.Id, body, postingAccounts);
        // 3. Post PendingApproval — the module never approves its own entries.
        await ledger.PostAsync(clientId, entry, ct);
        return deposit;
    }

    public async Task<CashDeposit> VoidDepositAsync(Guid clientId, Guid id, string? reason = null, CancellationToken ct = default)
    {
        CashDeposit deposit = await RequireDepositAsync(clientId, id, ct);
        if (deposit.Status != CashDepositStatus.Posted)
            throw new InvalidOperationException($"Only a posted cash deposit can be voided; {id} is {deposit.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, id, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for cash deposit {deposit.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(deposit.Date, reason ?? $"Voided cash deposit {id}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided cash deposit {id}"), ct);

        await deposits.VoidAsync(clientId, id, ct);
        return await RequireDepositAsync(clientId, id, ct);
    }

    public async Task<CashDeposit?> GetDepositAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDeposit? doc = await deposits.GetAsync(clientId, id, ct);
        if (doc is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, id, ct);
        return doc.Status == CashDepositStatus.Void || CashLedgerStatus.ShowsVoided(entries)
            ? doc with { Status = CashDepositStatus.Void }
            : doc;
    }

    // ── List reads (ledger-truth status, one batched ledger call per page) ─────

    public async Task<PagedResponse<CashDeposit>> ListDepositsAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<CashDeposit> page = await deposits.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(d => d.Id), ct);
        List<CashDeposit> overlaid = page.Items
            .Select(d => d.Status == CashDepositStatus.Void || CashLedgerStatus.ShowsVoided(byRef[d.Id].ToList())
                ? d with { Status = CashDepositStatus.Void }
                : d)
            .ToList();
        return new PagedResponse<CashDeposit>(overlaid, page.Total, page.Skip, page.Limit);
    }

    public async Task<PagedResponse<CashDisbursement>> ListDisbursementsAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<CashDisbursement> page = await disbursements.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(d => d.Id), ct);
        List<CashDisbursement> overlaid = page.Items
            .Select(d => d.Status == CashDisbursementStatus.Void || CashLedgerStatus.ShowsVoided(byRef[d.Id].ToList())
                ? d with { Status = CashDisbursementStatus.Void }
                : d)
            .ToList();
        return new PagedResponse<CashDisbursement>(overlaid, page.Total, page.Skip, page.Limit);
    }

    private async Task<ILookup<Guid, EntryResponse>> EntriesByRefAsync(Guid clientId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        List<Guid> refs = ids.ToList();
        IReadOnlyList<EntryResponse> entries = refs.Count == 0
            ? []
            : await ledger.GetEntriesBySourceRefsAsync(clientId, refs, ct);
        return entries.Where(e => e.SourceRef is not null).ToLookup(e => e.SourceRef!.Value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<CashDisbursement> RequireDisbursementAsync(Guid clientId, Guid id, CancellationToken ct) =>
        await disbursements.GetAsync(clientId, id, ct) ?? throw new InvalidOperationException($"Cash disbursement {id} not found.");

    private async Task<CashDeposit> RequireDepositAsync(Guid clientId, Guid id, CancellationToken ct) =>
        await deposits.GetAsync(clientId, id, ct) ?? throw new InvalidOperationException($"Cash deposit {id} not found.");
}
