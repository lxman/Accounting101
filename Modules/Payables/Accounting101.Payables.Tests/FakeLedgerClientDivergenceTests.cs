using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Payables.Tests;

public class FakeLedgerClientDivergenceTests
{
    /// <summary>
    /// Production answers 422 when a retry key is reused for DIFFERENT content; the fake must too.
    /// Without this, a divergent-retry test written against the fake passes vacuously — it looks like
    /// protection and provides none.
    /// </summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_different_content_is_rejected()
    {
        FakeLedgerClient ledger = new();
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

        // A financially substantive divergence — a line Amount change — not Memo/Reference, which
        // production (EntryComparison.SameFinancialContent) deliberately ignores as descriptive-only
        // fields; a Memo/Reference-only retry is a successful replay in production, not a 422.
        PostEntryRequest divergent = first with
        {
            Lines =
            [
                first.Lines[0] with { Amount = 200m },
                first.Lines[1],
            ],
        };

        await Assert.ThrowsAnyAsync<Exception>(() => ledger.PostAsync(clientId, divergent));
    }

    /// <summary>An identical re-post still dedupes — only DIVERGENT content is rejected.</summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_identical_content_still_dedupes()
    {
        FakeLedgerClient ledger = new();
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

        Assert.Single(ledger.Posted, e => e.SourceRef == sourceRef);
    }

    /// <summary>
    /// The path real Payables module code actually exercises: all three posting builders in
    /// BillPosting.cs set a deterministic <c>PostEntryRequest.Id</c> via
    /// <c>EntryIdentity.ForSource(SourceType, docId)</c>, so a real second post always hits the
    /// Id-keyed fast path in FakeLedgerClient.PostAsync, not the SourceRef-only path the sibling
    /// fact above exercises (which real module code never emits, since Id is never null there). This
    /// fact pins that the Id-keyed path rejects divergent content too, not just replay identically.
    /// </summary>
    [Fact]
    public async Task Posting_the_same_id_with_different_content_is_rejected()
    {
        FakeLedgerClient ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();
        Guid id = EntryIdentity.ForSource("Test", sourceRef);

        PostEntryRequest first = new(
            Id: id, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, first);

        PostEntryRequest divergent = first with
        {
            Lines =
            [
                first.Lines[0] with { Amount = 200m },
                first.Lines[1],
            ],
        };

        await Assert.ThrowsAnyAsync<Exception>(() => ledger.PostAsync(clientId, divergent));
    }

    /// <summary>An identical re-post under the same (real-path) Id still dedupes.</summary>
    [Fact]
    public async Task Posting_the_same_id_with_identical_content_still_dedupes()
    {
        FakeLedgerClient ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();
        Guid id = EntryIdentity.ForSource("Test", sourceRef);

        PostEntryRequest entry = new(
            Id: id, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, entry);
        await ledger.PostAsync(clientId, entry);

        Assert.Single(ledger.Posted, e => e.SourceRef == sourceRef);
    }
}
