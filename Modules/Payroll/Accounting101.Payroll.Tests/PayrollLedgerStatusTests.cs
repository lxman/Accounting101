using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll.Tests;

public sealed class PayrollLedgerStatusTests
{
    private static EntryResponse Entry(Guid id, string status, Guid? reversalOf, Guid sourceRef) =>
        new(id, 0, default, "Standard", status, "Posted", 0, null, null, reversalOf, null, [], sourceRef, "PayrollRun");

    [Fact]
    public void Active_posted_entry_is_not_voided()
    {
        Guid src = Guid.NewGuid();
        var entries = new[] { Entry(Guid.NewGuid(), "Active", null, src) };
        Assert.False(PayrollLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void Withdrawn_pending_primary_is_voided()
    {
        Guid src = Guid.NewGuid();
        var entries = new[] { Entry(Guid.NewGuid(), "Voided", null, src) };
        Assert.True(PayrollLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void Reversed_after_posting_is_voided()
    {
        Guid src = Guid.NewGuid();
        Guid primary = Guid.NewGuid();
        var entries = new[]
        {
            Entry(primary, "Active", null, src),                 // original stays Active
            Entry(Guid.NewGuid(), "Active", primary, src),       // reversal points at the original
        };
        Assert.True(PayrollLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void No_entries_is_not_voided_so_caller_falls_back_to_envelope()
    {
        Assert.False(PayrollLedgerStatus.ShowsVoided([]));
    }
}
