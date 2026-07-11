using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public sealed class ChartReadinessCheckerTests
{
    private static AccountResponse Acct(Guid id, string type, string[] dims, bool active = true) =>
        new(id, "1000", "Acct", type, null, true, null, dims, null, false, active, "Debit", false);

    private static AccountRequirement Req(Guid id, string? type, string[] dims) =>
        new(id, "Label", type, dims);

    [Fact]
    public void Ok_when_account_exists_active_right_type_and_covers_dims()
    {
        Guid id = Guid.NewGuid();
        ChartReadinessReport r = ChartReadinessChecker.Check(
            [Req(id, "Asset", ["Item"])], [Acct(id, "Asset", ["Item"])], "inventory");
        Assert.True(r.Ready);
        Assert.Equal(AccountReadinessStatus.Ok, Assert.Single(r.Accounts).Status);
    }

    [Fact]
    public void Missing_when_no_account_with_that_id()
    {
        ChartReadinessReport r = ChartReadinessChecker.Check(
            [Req(Guid.NewGuid(), "Asset", [])], [], "inventory");
        Assert.False(r.Ready);
        Assert.Equal(AccountReadinessStatus.Missing, Assert.Single(r.Accounts).Status);
    }

    [Fact]
    public void MissingDimensions_when_account_lacks_a_required_dimension()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", ["Item"])], [Acct(id, "Asset", [])], "inventory").Accounts);
        Assert.Equal(AccountReadinessStatus.MissingDimensions, res.Status);
    }

    [Fact]
    public void Subset_semantics_ok_when_account_requires_more_dimensions_than_needed()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", ["Customer"])], [Acct(id, "Asset", ["Customer", "Invoice"])], "receivables").Accounts);
        Assert.Equal(AccountReadinessStatus.Ok, res.Status);
    }

    [Fact]
    public void WrongType_when_type_differs()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", [])], [Acct(id, "Liability", [])], "cash").Accounts);
        Assert.Equal(AccountReadinessStatus.WrongType, res.Status);
    }

    [Fact]
    public void Inactive_when_account_is_deactivated()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", [])], [Acct(id, "Asset", [], active: false)], "cash").Accounts);
        Assert.Equal(AccountReadinessStatus.Inactive, res.Status);
    }

    [Fact]
    public void Missing_takes_precedence_and_report_not_ready_with_mixed_results()
    {
        Guid ok = Guid.NewGuid(), gone = Guid.NewGuid();
        ChartReadinessReport r = ChartReadinessChecker.Check(
            [Req(ok, "Asset", []), Req(gone, "Asset", ["Item"])],
            [Acct(ok, "Asset", [])], "inventory");
        Assert.False(r.Ready);
        Assert.Equal(AccountReadinessStatus.Ok, r.Accounts[0].Status);
        Assert.Equal(AccountReadinessStatus.Missing, r.Accounts[1].Status);
    }

    [Fact]
    public void Null_expected_type_skips_type_check()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, null, [])], [Acct(id, "Liability", [])], "cash").Accounts);
        Assert.Equal(AccountReadinessStatus.Ok, res.Status);
    }
}
