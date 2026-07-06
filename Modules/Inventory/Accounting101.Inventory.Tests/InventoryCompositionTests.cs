using System.Net;

namespace Accounting101.Inventory.Tests;

/// <summary>Proves the seven-module composition (engine + Receivables + Payables + Payroll + Cash +
/// Reconciliation + FixedAssets + Inventory) still boots with the inventory module registered. The host
/// has no dedicated liveness route, so this asserts a bogus inventory route fails cleanly (401/404 —
/// routing/auth pipeline alive, no routes mapped yet) rather than 500ing (composition/DI broken).</summary>
public sealed class InventoryCompositionTests(InventoryHostFixture fx) : IClassFixture<InventoryHostFixture>
{
    [Fact]
    public async Task Host_boots_with_inventory_module_registered()
    {
        HttpClient http = fx.CreateClient();
        HttpResponseMessage res = await http.GetAsync($"/clients/{Guid.NewGuid()}/items");

        Assert.True(
            res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized,
            $"expected 404/401, got {(int)res.StatusCode} {res.StatusCode}");
    }
}
