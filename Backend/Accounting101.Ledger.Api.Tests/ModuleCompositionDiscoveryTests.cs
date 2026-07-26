using Accounting101.ModuleKit.Api;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Runs in the test process, whose deps.json includes every project the Host references
/// transitively — so discovery here sees exactly what the Host sees at startup.</summary>
public sealed class ModuleCompositionDiscoveryTests
{
    [Fact]
    public void Discovers_at_least_the_seven_public_module_compositions()
    {
        string[] found = ModuleCompositionDiscovery.DiscoverAll()
            .Select(m => m.GetType().Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Superset, not equality: composition is deployment-dependent. A Modules.Private/ overlay
        // (EInvoicing today) legitimately adds more, and this checkout may or may not have one — an
        // exact-set assertion fails purely on whether the overlay happens to be present.
        //
        // The original purpose survives unchanged: a pruned or undiscovered PUBLIC module still fails
        // here loudly, which is the whole reason this test exists.
        string[] expected =
        [
            "CashComposition", "FixedAssetsComposition", "InventoryComposition",
            "PayablesComposition", "PayrollComposition", "ReceivablesComposition",
            "ReconciliationComposition",
        ];
        string[] missing = [.. expected.Except(found).OrderBy(n => n, StringComparer.Ordinal)];

        Assert.True(
            missing.Length == 0,
            $"Undiscovered public module composition(s): {string.Join(", ", missing)}. "
            + $"Discovered: {string.Join(", ", found)}.");
    }

    [Fact]
    public void Discovery_order_is_deterministic()
    {
        Assert.Equal(
            ModuleCompositionDiscovery.DiscoverAll().Select(m => m.GetType().FullName).ToArray(),
            ModuleCompositionDiscovery.DiscoverAll().Select(m => m.GetType().FullName).ToArray());
    }
}
