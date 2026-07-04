using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

/// <summary>
/// An in-memory stand-in for the engine: records what was posted, models approve/reverse, and resolves
/// entries by their source back-link — enough to drive and assert the module's lifecycle without HTTP.
/// </summary>
internal sealed class FakeLedgerClient : ILedgerClient
{
    private readonly Dictionary<Guid, EntryResponse> _entries = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;

    /// <summary>Flips true the moment either <see cref="ReverseAsync"/> or <see cref="VoidAsync"/> is called.</summary>
    public bool ReversedOrWithdrawn { get; private set; }

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null);
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse original = _entries[entryId];
        var id = Guid.NewGuid();
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "PendingApproval", reversalOf: entryId);
        _entries[id] = reversal;
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
        Task.FromResult<IReadOnlyList<EntryResponse>>(_entries.Values.Where(e => e.SourceRef == sourceRef).ToList());

    private static EntryResponse Entry(Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf) =>
        new(id, 0, default, "Standard", "Active", posting, 0, null, null, reversalOf, null, [], sourceRef, sourceType);
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
            AccumulatedDepreciation = 0m,
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

    public Task ApplyDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default)
    {
        foreach (DepreciationRunLine line in lines)
            _assets[line.AssetId] = _assets[line.AssetId] with
            {
                AccumulatedDepreciation = _assets[line.AssetId].AccumulatedDepreciation + line.Amount,
            };
        return Task.CompletedTask;
    }

    public Task ReverseDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default)
    {
        foreach (DepreciationRunLine line in lines)
            _assets[line.AssetId] = _assets[line.AssetId] with
            {
                AccumulatedDepreciation = _assets[line.AssetId].AccumulatedDepreciation - line.Amount,
            };
        return Task.CompletedTask;
    }

    public Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct = default)
    {
        if (!_assets.TryGetValue(assetId, out Asset? a))
            return Task.FromResult(new DisposeStamp(DisposeOutcome.NotFound, null, 0m));
        if (a.Status != AssetStatus.Active)
            return Task.FromResult(new DisposeStamp(DisposeOutcome.NotActive, null, a.AccumulatedDepreciation));
        decimal prior = a.AccumulatedDepreciation;
        Asset disposed = a with { Status = AssetStatus.Disposed, AccumulatedDepreciation = finalAccumulated };
        _assets[assetId] = disposed;
        return Task.FromResult(new DisposeStamp(DisposeOutcome.Disposed, disposed, prior));
    }

    public Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct = default)
    {
        if (_assets.TryGetValue(assetId, out Asset? a))
            _assets[assetId] = a with { Status = AssetStatus.Active, AccumulatedDepreciation = restoreAccumulated };
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
