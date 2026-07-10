using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

/// <summary>
/// An in-memory stand-in for the engine: records what was posted, models approve/reverse, and resolves
/// entries by their source back-link — enough to drive and assert the module's lifecycle without HTTP.
/// </summary>
internal sealed class FakeLedgerClient : ILedgerClient
{
    private readonly Dictionary<Guid, EntryResponse> _entries = new();
    private readonly Dictionary<Guid, IReadOnlyList<PostLineRequest>> _linesById = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

    /// <summary>Flips true the moment either <see cref="ReverseAsync"/> or <see cref="VoidAsync"/> is called.</summary>
    public bool ReversedOrWithdrawn { get; private set; }

    /// <summary>When true, <see cref="GetEntriesBySourceRefAsync"/> returns an empty list — simulates a run
    /// stranded by a post that never landed.</summary>
    public bool ReturnNoEntries { get; set; }

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null, lines: entry.Lines);
        _linesById[id] = entry.Lines;
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse original = _entries[entryId];
        var id = Guid.NewGuid();
        // A reversal's fold effect is the original entry's lines with direction flipped — the original
        // entry stays Active (and still counted) so the pair nets to zero, exactly like the real engine.
        IReadOnlyList<PostLineRequest> reversedLines = _linesById.TryGetValue(entryId, out IReadOnlyList<PostLineRequest>? originalLines)
            ? originalLines.Select(l => l with { Direction = Flip(l.Direction) }).ToList()
            : [];
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "PendingApproval", reversalOf: entryId, lines: reversedLines);
        _entries[id] = reversal;
        _linesById[id] = reversedLines;
        return Task.FromResult(reversal);
    }

    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse voided = _entries[entryId] with { Status = "Voided" };
        _entries[entryId] = voided;
        return Task.FromResult(voided);
    }

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(ReturnNoEntries
            ? []
            : _entries.Values.Where(e => e.SourceRef == sourceRef).ToList());

    /// <summary>
    /// A real per-dimension fold over every posted entry's lines, mirroring the real engine's subledger
    /// (debit-positive, grouped by dimension value) closely enough to drive FA unit tests. This fold does
    /// NOT gate on approval (Posting == "Posted") — it counts every Active entry regardless of approval
    /// state, matching this fake's existing "immediate effect" semantics; the approval-gated fold behavior
    /// is proven separately by the real HTTP-backed engine tests. Voided entries are excluded; a reversed
    /// entry stays Active and its reversal's negated lines net it to zero, same as production.
    /// </summary>
    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        Dictionary<Guid, decimal> totals = new();
        foreach ((Guid id, EntryResponse response) in _entries)
        {
            if (response.Status != "Active") continue;
            if (!_linesById.TryGetValue(id, out IReadOnlyList<PostLineRequest>? lines)) continue;
            foreach (PostLineRequest line in lines)
            {
                if (line.AccountId != account) continue;
                if (line.Dimensions is null || !line.Dimensions.TryGetValue(dimension, out Guid dimValue)) continue;
                decimal signed = line.Direction == "Debit" ? line.Amount : -line.Amount;
                totals[dimValue] = totals.GetValueOrDefault(dimValue) + signed;
            }
        }
        return Task.FromResult<IReadOnlyList<SubledgerLineResponse>>(
            totals.Select(kv => new SubledgerLineResponse(account, kv.Key, kv.Value)).ToList());
    }

    private static string Flip(string direction) => direction == "Debit" ? "Credit" : "Debit";

    private static EntryResponse Entry(
        Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf,
        IReadOnlyList<PostLineRequest>? lines = null)
    {
        IReadOnlyList<EntryLineResponse> mapped = (lines ?? []).Select(l =>
            new EntryLineResponse(l.AccountId, l.Direction, l.Amount, l.Dimensions ?? new Dictionary<string, Guid>(), null)).ToList();
        return new(id, 0, default, "Standard", "Active", posting, mapped.Count, null, null, reversalOf, null, mapped, sourceRef, sourceType);
    }
}

/// <summary>An in-memory asset register: a dictionary of active assets plus a set of deactivated ids,
/// enough to drive the run service's eligibility scan and accumulated-depreciation mutations.</summary>
internal sealed class InMemoryAssetStore : IAssetStore
{
    private readonly Dictionary<Guid, Asset> _assets = new();
    private readonly HashSet<Guid> _deactivated = [];

    public Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Asset asset = new()
        {
            Id = Guid.NewGuid(),
            Description = body.Description,
            AcquisitionCost = body.AcquisitionCost,
            InServiceDate = body.InServiceDate,
            UsefulLifeMonths = body.UsefulLifeMonths,
            SalvageValue = body.SalvageValue,
            Method = body.Method,
            DecliningBalanceFactor = body.DecliningBalanceFactor,
            Status = AssetStatus.Active,
        };
        _assets[asset.Id] = asset;
        return Task.FromResult(asset);
    }

    public Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by FixedAssetsRunServiceTests.");

    public Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        if (!_assets.ContainsKey(assetId)) return Task.FromResult(DeactivateResult.NotFound);
        return Task.FromResult(_deactivated.Add(assetId) ? DeactivateResult.Deactivated : DeactivateResult.AlreadyInactive);
    }

    public Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        if (!_assets.ContainsKey(assetId)) return Task.FromResult(ReactivateResult.NotFound);
        return Task.FromResult(_deactivated.Remove(assetId) ? ReactivateResult.Reactivated : ReactivateResult.AlreadyActive);
    }

    public Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        Task.FromResult(_assets.GetValueOrDefault(assetId));

    public Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        IEnumerable<Asset> all = _assets.Values.Where(a => includeInactive || !_deactivated.Contains(a.Id));
        List<Asset> ordered = (descending ? all.OrderByDescending(a => a.InServiceDate) : all.OrderBy(a => a.InServiceDate)).ToList();
        List<Asset> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<Asset>(items, ordered.Count, skip, limit));
    }

    public Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        if (!_assets.TryGetValue(assetId, out Asset? a))
            return Task.FromResult(new DisposeStamp(DisposeOutcome.NotFound, null));
        if (a.Status != AssetStatus.Active)
            return Task.FromResult(new DisposeStamp(DisposeOutcome.NotActive, null));
        Asset disposed = a with { Status = AssetStatus.Disposed };
        _assets[assetId] = disposed;
        return Task.FromResult(new DisposeStamp(DisposeOutcome.Disposed, disposed));
    }

    public Task ReinstateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        if (_assets.TryGetValue(assetId, out Asset? a))
            _assets[assetId] = a with { Status = AssetStatus.Active };
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory depreciation-run store: assigns incrementing DR-##### numbers and filters
/// voided runs out of the period-guard and LIFO-void queries the run service depends on.</summary>
internal sealed class InMemoryDepreciationRunStore : IDepreciationRunStore
{
    private readonly List<DepreciationRun> _runs = [];
    private int _next;

    public Task<DepreciationRun> RecordAsync(Guid clientId, DepreciationRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        DepreciationRun run = new()
        {
            Id = Guid.NewGuid(),
            Number = $"DR-{Interlocked.Increment(ref _next):D5}",
            Period = body.Period,
            EffectiveDate = body.EffectiveDate,
            Memo = body.Memo,
            Lines = body.Lines,
            Total = body.Total,
            Status = DepreciationRunStatus.Posted,
        };
        _runs.Add(run);
        return Task.FromResult(run);
    }

    public Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        int index = _runs.FindIndex(r => r.Id == runId);
        if (index >= 0) _runs[index] = _runs[index] with { Status = DepreciationRunStatus.Voided };
        return Task.CompletedTask;
    }

    public Task<DepreciationRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        Task.FromResult(_runs.FirstOrDefault(r => r.Id == runId));

    public Task<PagedResponse<DepreciationRun>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<DepreciationRun> all = _runs.Where(r => includeVoided || r.Status != DepreciationRunStatus.Voided);
        List<DepreciationRun> ordered = (descending ? all.OrderByDescending(r => r.Number) : all.OrderBy(r => r.Number)).ToList();
        List<DepreciationRun> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<DepreciationRun>(items, ordered.Count, skip, limit));
    }

    public Task<DepreciationRun?> GetByPeriodAsync(Guid clientId, DepreciationPeriod period, CancellationToken ct = default) =>
        Task.FromResult(_runs.FirstOrDefault(r => r.Status != DepreciationRunStatus.Voided && r.Period == period));

    public Task<DepreciationRun?> GetLatestAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(_runs.Where(r => r.Status != DepreciationRunStatus.Voided)
            .OrderByDescending(r => r.Number)
            .FirstOrDefault());
}

/// <summary>An in-memory disposal store: assigns incrementing DP-##### numbers and resolves the active
/// (non-voided) disposal for an asset — enough to drive and assert the disposal service's lifecycle.</summary>
internal sealed class InMemoryDisposalStore : IDisposalStore
{
    private readonly List<Disposal> _disposals = [];
    private int _next;

    public Task<Disposal> RecordAsync(Guid clientId, DisposalBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Disposal disposal = new()
        {
            Id = Guid.NewGuid(),
            Number = $"DP-{Interlocked.Increment(ref _next):D5}",
            AssetId = body.AssetId,
            DisposalDate = body.DisposalDate,
            Proceeds = body.Proceeds,
            CatchUpDepreciation = body.CatchUpDepreciation,
            AccumulatedBeforeDisposal = body.AccumulatedBeforeDisposal,
            AccumulatedAtDisposal = body.AccumulatedAtDisposal,
            NetBookValue = body.NetBookValue,
            GainLoss = body.GainLoss,
            Memo = body.Memo,
            Status = DisposalStatus.Posted,
        };
        _disposals.Add(disposal);
        return Task.FromResult(disposal);
    }

    public Task VoidAsync(Guid clientId, Guid disposalId, CancellationToken ct = default)
    {
        int index = _disposals.FindIndex(d => d.Id == disposalId);
        if (index >= 0) _disposals[index] = _disposals[index] with { Status = DisposalStatus.Voided };
        return Task.CompletedTask;
    }

    public Task<Disposal?> GetAsync(Guid clientId, Guid disposalId, CancellationToken ct = default) =>
        Task.FromResult(_disposals.FirstOrDefault(d => d.Id == disposalId));

    public Task<PagedResponse<Disposal>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<Disposal> all = _disposals.Where(d => includeVoided || d.Status != DisposalStatus.Voided);
        List<Disposal> ordered = (descending ? all.OrderByDescending(d => d.Number) : all.OrderBy(d => d.Number)).ToList();
        List<Disposal> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<Disposal>(items, ordered.Count, skip, limit));
    }

    public Task<Disposal?> GetActiveByAssetAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        Task.FromResult(_disposals.FirstOrDefault(d => d.AssetId == assetId && d.Status != DisposalStatus.Voided));
}

/// <summary>Fixed pair of posting accounts, exposed as public properties for test assertions.</summary>
internal sealed class FixedAccountsProvider : IFixedAssetsAccountsProvider
{
    public Guid DepreciationExpenseAccountId { get; } = Guid.NewGuid();
    public Guid AccumulatedDepreciationAccountId { get; } = Guid.NewGuid();
    public Guid AssetCostAccountId { get; } = Guid.NewGuid();
    public Guid DisposalProceedsAccountId { get; } = Guid.NewGuid();
    public Guid GainOnDisposalAccountId { get; } = Guid.NewGuid();
    public Guid LossOnDisposalAccountId { get; } = Guid.NewGuid();

    public Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId = DepreciationExpenseAccountId,
            AccumulatedDepreciationAccountId = AccumulatedDepreciationAccountId,
            AssetCostAccountId = AssetCostAccountId,
            DisposalProceedsAccountId = DisposalProceedsAccountId,
            GainOnDisposalAccountId = GainOnDisposalAccountId,
            LossOnDisposalAccountId = LossOnDisposalAccountId,
        });
}
