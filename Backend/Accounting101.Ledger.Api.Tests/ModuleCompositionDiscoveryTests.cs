using Accounting101.ModuleKit.Api;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Runs in the test process, whose deps.json includes every project the Host references
/// transitively — so discovery here sees exactly what the Host sees at startup.</summary>
public sealed class ModuleCompositionDiscoveryTests
{
    [Fact]
    public void Discovers_exactly_the_seven_module_compositions()
    {
        string[] found = ModuleCompositionDiscovery.DiscoverAll()
            .Select(m => m.GetType().Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Pins the full set: a pruned/undiscovered module fails here loudly, and adding an
        // eighth in-repo module must update this expectation deliberately.
        string[] expected =
        [
            "CashComposition", "FixedAssetsComposition", "InventoryComposition",
            "PayablesComposition", "PayrollComposition", "ReceivablesComposition",
            "ReconciliationComposition",
        ];
        Assert.Equal(expected, found);
    }

    [Fact]
    public void Discovery_order_is_deterministic()
    {
        Assert.Equal(
            ModuleCompositionDiscovery.DiscoverAll().Select(m => m.GetType().FullName).ToArray(),
            ModuleCompositionDiscovery.DiscoverAll().Select(m => m.GetType().FullName).ToArray());
    }
}
