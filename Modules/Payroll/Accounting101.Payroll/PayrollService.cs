using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll;

/// <summary>The payroll lifecycle: record a run (persist evidentiary doc + post PendingApproval entry)
/// and void it (reverse the entry if posted, or withdraw if still pending). Same pair for tax
/// remittances. The module never self-approves.</summary>
public sealed class PayrollService(
    IPayrollRunStore runs,
    ITaxRemittanceStore remittances,
    IPayrollAccountsProvider accounts,
    ILedgerClient ledger)
{
    // ── Payroll Runs ────────────────────────────────────────────────────────

    public async Task<PayrollRun> RecordRunAsync(Guid clientId, PayrollRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        PayrollRun run = await runs.RecordAsync(clientId, body, ct);
        // 2. Resolve posting accounts and compose the balanced entry via the Task-1 recipe.
        PayrollPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);
        PostEntryRequest entry = PayrollPosting.ComposePayrollRun(run.Id, body, postingAccounts);
        // 3. Post PendingApproval — the module never approves its own entries.
        await ledger.PostAsync(clientId, entry, ct);
        return run;
    }

    public async Task<PayrollRun> VoidRunAsync(Guid clientId, Guid runId, string? reason = null, CancellationToken ct = default)
    {
        PayrollRun run = await RequireRunAsync(clientId, runId, ct);
        if (run.Status != PayrollRunStatus.Posted)
            throw new InvalidOperationException($"Only a posted payroll run can be voided; {runId} is {run.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, runId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for payroll run {run.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(run.PayDate, reason ?? $"Voided payroll run {runId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided payroll run {runId}"), ct);

        await runs.VoidAsync(clientId, runId, ct);
        return await RequireRunAsync(clientId, runId, ct);
    }

    public async Task<PayrollRun?> GetRunAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        PayrollRun? run = await runs.GetAsync(clientId, runId, ct);
        if (run is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, runId, ct);
        return run.Status == PayrollRunStatus.Void || PayrollLedgerStatus.ShowsVoided(entries)
            ? run with { Status = PayrollRunStatus.Void }
            : run;
    }

    // ── Tax Remittances ─────────────────────────────────────────────────────

    public async Task<TaxRemittance> RecordRemittanceAsync(Guid clientId, TaxRemittanceBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        TaxRemittance remittance = await remittances.RecordAsync(clientId, body, ct);
        // 2. Resolve posting accounts and compose the balanced entry via the Task-1 recipe.
        PayrollPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);
        PostEntryRequest entry = PayrollPosting.ComposeTaxRemittance(remittance.Id, body, postingAccounts);
        // 3. Post PendingApproval — the module never approves its own entries.
        await ledger.PostAsync(clientId, entry, ct);
        return remittance;
    }

    public async Task<TaxRemittance> VoidRemittanceAsync(Guid clientId, Guid remittanceId, string? reason = null, CancellationToken ct = default)
    {
        TaxRemittance remittance = await RequireRemittanceAsync(clientId, remittanceId, ct);
        if (remittance.Status != TaxRemittanceStatus.Posted)
            throw new InvalidOperationException($"Only a posted tax remittance can be voided; {remittanceId} is {remittance.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, remittanceId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for tax remittance {remittance.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(remittance.PayDate, reason ?? $"Voided tax remittance {remittanceId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided tax remittance {remittanceId}"), ct);

        await remittances.VoidAsync(clientId, remittanceId, ct);
        return await RequireRemittanceAsync(clientId, remittanceId, ct);
    }

    public async Task<TaxRemittance?> GetRemittanceAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default)
    {
        TaxRemittance? remittance = await remittances.GetAsync(clientId, remittanceId, ct);
        if (remittance is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, remittanceId, ct);
        return remittance.Status == TaxRemittanceStatus.Void || PayrollLedgerStatus.ShowsVoided(entries)
            ? remittance with { Status = TaxRemittanceStatus.Void }
            : remittance;
    }

    // ── List reads (ledger-truth status, one batched ledger call per page) ─────

    public async Task<PagedResponse<PayrollRun>> ListRunsAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<PayrollRun> page = await runs.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(r => r.Id), ct);
        List<PayrollRun> overlaid = page.Items
            .Select(r => r.Status == PayrollRunStatus.Void || PayrollLedgerStatus.ShowsVoided(byRef[r.Id].ToList())
                ? r with { Status = PayrollRunStatus.Void }
                : r)
            .ToList();
        return new PagedResponse<PayrollRun>(overlaid, page.Total, page.Skip, page.Limit);
    }

    public async Task<PagedResponse<TaxRemittance>> ListRemittancesAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<TaxRemittance> page = await remittances.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(r => r.Id), ct);
        List<TaxRemittance> overlaid = page.Items
            .Select(r => r.Status == TaxRemittanceStatus.Void || PayrollLedgerStatus.ShowsVoided(byRef[r.Id].ToList())
                ? r with { Status = TaxRemittanceStatus.Void }
                : r)
            .ToList();
        return new PagedResponse<TaxRemittance>(overlaid, page.Total, page.Skip, page.Limit);
    }

    private async Task<ILookup<Guid, EntryResponse>> EntriesByRefAsync(Guid clientId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        List<Guid> refs = ids.ToList();
        IReadOnlyList<EntryResponse> entries = refs.Count == 0
            ? []
            : await ledger.GetEntriesBySourceRefsAsync(clientId, refs, ct);
        return entries.Where(e => e.SourceRef is not null).ToLookup(e => e.SourceRef!.Value);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<PayrollRun> RequireRunAsync(Guid clientId, Guid runId, CancellationToken ct) =>
        await runs.GetAsync(clientId, runId, ct) ?? throw new InvalidOperationException($"Payroll run {runId} not found.");

    private async Task<TaxRemittance> RequireRemittanceAsync(Guid clientId, Guid remittanceId, CancellationToken ct) =>
        await remittances.GetAsync(clientId, remittanceId, ct) ?? throw new InvalidOperationException($"Tax remittance {remittanceId} not found.");
}
