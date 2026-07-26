using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// Reconciliation's ledger fake is named <see cref="FakePostingLedger"/>, not FakeLedgerClient (unlike
/// every other module's copy) — see Fakes.cs. It also has no history: <see cref="FakePostingLedger.Posted"/>
/// is the single most-recently-posted request, not a list, so these facts are adapted from the shared
/// template rather than reusing the "Assert.Single(Posted, ...)" shape the other modules use.
/// </summary>
public class FakePostingLedgerDivergenceTests
{
    /// <summary>
    /// Production answers 422 when a retry key is reused for DIFFERENT content; the fake must too.
    /// Without this, a divergent-retry test written against the fake passes vacuously — it looks like
    /// protection and provides none.
    /// </summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_different_content_is_rejected()
    {
        FakePostingLedger ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();

        PostEntryRequest first = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, first);

        PostEntryRequest divergent = first with { Memo = "changed" };

        await Assert.ThrowsAnyAsync<Exception>(() => ledger.PostAsync(clientId, divergent));
    }

    /// <summary>An identical re-post still dedupes — only DIVERGENT content is rejected.</summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_identical_content_still_dedupes()
    {
        FakePostingLedger ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, entry);
        await ledger.PostAsync(clientId, entry);

        Assert.Equal(sourceRef, ledger.Posted?.SourceRef);
    }
}
