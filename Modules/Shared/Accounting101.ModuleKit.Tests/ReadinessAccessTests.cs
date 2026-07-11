using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public class ReadinessAccessTests
{
    [Theory]
    [InlineData("receivables", "ar.read")]
    [InlineData("payables", "ap.read")]
    [InlineData("payroll", "payroll.read")]
    [InlineData("cash", "cash.read")]
    [InlineData("fixedassets", "fixedassets.read")]
    [InlineData("inventory", "inventory.read")]
    public void ReadCapabilityFor_maps_each_module_to_its_area_read(string key, string expected) =>
        Assert.Equal(expected, ReadinessAccess.ReadCapabilityFor(key));

    [Fact]
    public void ReadCapabilityFor_unknown_key_is_null() =>
        Assert.Null(ReadinessAccess.ReadCapabilityFor("reconciliation"));

    [Fact]
    public void Holding_the_modules_read_capability_allows_that_module_only()
    {
        Assert.True(ReadinessAccess.Allows("cash", deploymentAdmin: false, ["cash.read"]));
        Assert.False(ReadinessAccess.Allows("payroll", deploymentAdmin: false, ["cash.read"]));
    }

    [Fact]
    public void Missing_the_capability_denies()
    {
        Assert.False(ReadinessAccess.Allows("cash", deploymentAdmin: false, ["ar.read", "gl.read"]));
        Assert.False(ReadinessAccess.Allows("cash", deploymentAdmin: false, []));
    }

    [Fact]
    public void Deployment_admin_is_allowed_any_module_without_the_capability()
    {
        Assert.True(ReadinessAccess.Allows("payroll", deploymentAdmin: true, []));
        Assert.True(ReadinessAccess.Allows("inventory", deploymentAdmin: true, ["ar.read"]));
    }

    [Fact]
    public void Client_admin_capability_is_allowed_any_module_without_the_read_capability()
    {
        Assert.True(ReadinessAccess.Allows("payroll", deploymentAdmin: false, ["admin.client"]));
        Assert.True(ReadinessAccess.Allows("cash", deploymentAdmin: false, ["admin.client"]));
    }

    [Fact]
    public void Unknown_module_denies_unless_admin()
    {
        Assert.False(ReadinessAccess.Allows("nope", deploymentAdmin: false, ["cash.read"]));
        Assert.True(ReadinessAccess.Allows("nope", deploymentAdmin: true, []));
        Assert.True(ReadinessAccess.Allows("nope", deploymentAdmin: false, ["admin.client"]));
    }
}
