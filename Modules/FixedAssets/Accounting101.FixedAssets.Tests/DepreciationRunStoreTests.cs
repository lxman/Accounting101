using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class DepreciationRunStoreTests(DepreciationRunStoreFixture fixture)
    : IClassFixture<DepreciationRunStoreFixture>
{
    private DocumentDepreciationRunStore Store() => new(fixture.Store);

    private static DepreciationRunBody Body(int year, int month, params (Guid asset, decimal amt)[] lines)
    {
        List<DepreciationRunLine> runLines = lines.Select(l => new DepreciationRunLine(l.asset, l.amt)).ToList();
        return new DepreciationRunBody(
            new DepreciationPeriod(year, month),
            new DepreciationPeriod(year, month).LastDay(),
            Memo: null,
            Lines: runLines,
            Total: runLines.Sum(l => l.Amount));
    }

    [Fact]
    public async Task Record_assigns_a_number_and_posted_status_and_round_trips()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();
        Guid asset = Guid.NewGuid();

        DepreciationRun run = await store.RecordAsync(clientId, Body(2026, 1, (asset, 500m)), default);

        Assert.NotNull(run.Number);
        Assert.Equal(DepreciationRunStatus.Posted, run.Status);
        Assert.Equal(500m, run.Total);
        DepreciationRun? fetched = await store.GetAsync(clientId, run.Id, default);
        Assert.NotNull(fetched);
        Assert.Equal(new DepreciationPeriod(2026, 1), fetched!.Period);
        Assert.Equal(asset, Assert.Single(fetched.Lines).AssetId);
    }

    [Fact]
    public async Task GetByPeriod_finds_a_non_voided_run_and_ignores_voided()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();

        DepreciationRun jan = await store.RecordAsync(clientId, Body(2026, 1, (Guid.NewGuid(), 100m)), default);
        Assert.NotNull(await store.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 1), default));
        Assert.Null(await store.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 2), default));

        await store.VoidAsync(clientId, jan.Id, default);
        Assert.Null(await store.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 1), default));
    }

    [Fact]
    public async Task GetLatest_returns_the_most_recent_non_voided_run()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();

        DepreciationRun jan = await store.RecordAsync(clientId, Body(2026, 1, (Guid.NewGuid(), 100m)), default);
        DepreciationRun feb = await store.RecordAsync(clientId, Body(2026, 2, (Guid.NewGuid(), 100m)), default);

        DepreciationRun? latest = await store.GetLatestAsync(clientId, default);
        Assert.Equal(feb.Id, latest!.Id);

        await store.VoidAsync(clientId, feb.Id, default);
        DepreciationRun? afterVoid = await store.GetLatestAsync(clientId, default);
        Assert.Equal(jan.Id, afterVoid!.Id);
    }

    [Fact]
    public async Task Paged_list_excludes_voided_unless_requested()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();

        DepreciationRun a = await store.RecordAsync(clientId, Body(2026, 1, (Guid.NewGuid(), 100m)), default);
        await store.RecordAsync(clientId, Body(2026, 2, (Guid.NewGuid(), 100m)), default);
        await store.VoidAsync(clientId, a.Id, default);

        PagedResponse<DepreciationRun> excl = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.Equal(1, excl.Total);
        PagedResponse<DepreciationRun> incl = await store.GetByClientPagedAsync(clientId, 0, 50, true, true, default);
        Assert.Equal(2, incl.Total);
    }
}
