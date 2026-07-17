namespace Accounting101.Ledger.Api.Control;

/// <summary>One posting-account slot a module needs, with the metadata the admin screen renders. The
/// expected type and required dimensions are advisory (chart-readiness), not enforced at save time.</summary>
public sealed record PostingAccountSlot(
    string ModuleKey, string SlotKey, string Label, string ExpectedType, IReadOnlyList<string> RequiredDimensions);

/// <summary>The declared posting-account slots, per module (cash, payroll, payables, fixedassets, inventory
/// wired). Remaining modules fan out here (sourced from each module's *ChartRequirements).</summary>
public static class PostingAccountSlots
{
    public static readonly IReadOnlyList<PostingAccountSlot> All =
    [
        new("cash", "Cash", "Cash / bank account", "Asset", []),
        new("payroll", "SalariesExpense",     "Salaries Expense",      "Expense",   []),
        new("payroll", "PayrollTaxExpense",   "Payroll Tax Expense",   "Expense",   []),
        new("payroll", "Cash",                "Cash",                  "Asset",     []),
        new("payroll", "WithholdingsPayable", "Withholdings Payable",  "Liability", []),
        new("payroll", "PayrollTaxesPayable", "Payroll Taxes Payable", "Liability", []),
        new("payables", "Payable",       "Accounts Payable", "Liability", ["Vendor", "Bill"]),
        new("payables", "Cash",          "Cash",             "Asset",     []),
        new("payables", "VendorCredits", "Vendor Credits",   "Asset",     ["Vendor"]),
        new("fixedassets", "DepreciationExpense",     "Depreciation Expense",      "Expense", []),
        new("fixedassets", "AccumulatedDepreciation", "Accumulated Depreciation",  "Asset",   ["Asset"]),
        new("fixedassets", "AssetCost",               "Fixed Assets (asset cost)", "Asset",   []),
        new("fixedassets", "DisposalProceeds",        "Disposal Proceeds",         "Asset",   []),
        new("fixedassets", "GainOnDisposal",          "Gain on Disposal",          "Revenue", []),
        new("fixedassets", "LossOnDisposal",          "Loss on Disposal",          "Expense", []),
        new("inventory", "InventoryAsset",      "Inventory Asset",      "Asset",     ["Item"]),
        new("inventory", "Cogs",                "Cost of Goods Sold",   "Expense",   []),
        new("inventory", "GrniClearing",        "GRNI Clearing",        "Liability", []),
        new("inventory", "InventoryAdjustment", "Inventory Adjustment", "Expense",   []),
    ];

    public static IReadOnlyList<PostingAccountSlot> ForModule(string moduleKey) =>
        All.Where(s => s.ModuleKey == moduleKey).ToList();

    public static IReadOnlySet<string> ModuleKeys => All.Select(s => s.ModuleKey).ToHashSet();
}
