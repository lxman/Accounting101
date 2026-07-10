using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public sealed class LedgerTruthTests
{
    private static EntryResponse Entry(Guid id, string status, Guid? reversalOf = null) =>
        new(Id: id, SequenceNumber: 0, EffectiveDate: default, Type: "Standard", Status: status,
            Posting: "Posted", LineCount: 0, Supersedes: null, SupersededBy: null, ReversalOf: reversalOf,
            ReversedBy: null, Lines: []);

    [Fact]
    public void No_primary_entry_falls_back_to_envelope_returns_false() =>
        Assert.False(LedgerTruth.ShowsVoided([]));

    [Fact]
    public void Primary_withdrawn_while_pending_shows_voided()
    {
        EntryResponse primary = Entry(Guid.NewGuid(), "Voided");
        Assert.True(LedgerTruth.ShowsVoided([primary]));
    }

    [Fact]
    public void Reversal_of_a_primary_shows_voided()
    {
        Guid primaryId = Guid.NewGuid();
        EntryResponse primary = Entry(primaryId, "Active");
        EntryResponse reversal = Entry(Guid.NewGuid(), "Active", reversalOf: primaryId);
        Assert.True(LedgerTruth.ShowsVoided([primary, reversal]));
    }

    [Fact]
    public void Clean_active_primary_is_not_voided()
    {
        EntryResponse primary = Entry(Guid.NewGuid(), "Active");
        Assert.False(LedgerTruth.ShowsVoided([primary]));
    }
}
