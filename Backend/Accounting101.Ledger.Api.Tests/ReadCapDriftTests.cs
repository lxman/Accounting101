using Accounting101.Ledger.Api.Control;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ReadCapDriftTests
{
    [Theory]
    [InlineData("receivables")]
    [InlineData("payables")]
    [InlineData("payroll")]
    [InlineData("cash")]
    [InlineData("fixedassets")]
    [InlineData("inventory")]
    public void ReadinessAccess_readCap_matches_engine_read_capability(string moduleKey)
    {
        Assert.Equal(
            Capabilities.CapabilityForModule(moduleKey, ModuleAccessLevel.Read),
            ReadinessAccess.ReadCapabilityFor(moduleKey));
    }

    [Fact]
    public void Both_maps_return_null_for_an_unknown_module_key()
    {
        Assert.Null(ReadinessAccess.ReadCapabilityFor("ghost"));
        Assert.Null(Capabilities.CapabilityForModule("ghost", ModuleAccessLevel.Read));
    }
}
