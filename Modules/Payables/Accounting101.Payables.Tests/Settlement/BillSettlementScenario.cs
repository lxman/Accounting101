using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Payables.Tests;

/// <summary>Shared setup + assertion helpers for the payables money/settlement edge-case scenarios.</summary>
internal static class BillSettlementScenario
{
    internal static async Task SetUpChartAsync(HttpClient controller, Guid clientId, PayablesHostFixture f)
    {
        await PutAccountAsync(controller, clientId, f.PayableAccountId,          "2000", "Accounts Payable",  "Liability", "Vendor");
        await PutAccountAsync(controller, clientId, f.CashAccountId,             "1000", "Cash",              "Asset",     null);
        await PutAccountAsync(controller, clientId, f.VendorCreditsAccountId,    "1300", "Vendor Credits",    "Asset",     "Vendor");
        await PutAccountAsync(controller, clientId, f.RentExpenseAccountId,      "5200", "Rent Expense",      "Expense",   null);
        await PutAccountAsync(controller, clientId, f.UtilitiesExpenseAccountId, "5300", "Utilities Expense", "Expense",   null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    internal static async Task<Guid> CreateVendorAsync(HttpClient clerk, Guid clientId, string name = "PropCo")
    {
        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest(name, null)))
            .Content.ReadFromJsonAsync<Vendor>())!;
        return vendor.Id;
    }

    internal static async Task<Guid> DraftBillAsync(HttpClient clerk, Guid clientId, Guid vendorId, decimal amount, Guid expenseAccount)
    {
        DraftBillRequest req = new(vendorId, new DateOnly(2026, 3, 1), null, "REF", null,
            [new BillLineBody("Rent", amount, expenseAccount)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", req))
            .Content.ReadFromJsonAsync<Bill>())!;
        return draft.Id;
    }

    internal static async Task<Guid> EnterBillAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, decimal amount, Guid expenseAccount)
    {
        Guid id = await DraftBillAsync(clerk, clientId, vendorId, amount, expenseAccount);
        Bill entered = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered.Id);
        return entered.Id;
    }

    internal static async Task ApproveBySourceRefAsync(
        HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string substring)
    {
        Assert.Equal(status, resp.StatusCode);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(substring, problem!.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task AssertBalancedAsync(HttpClient http, Guid clientId, DateOnly asOf)
    {
        BalanceSheetResponse sheet = (await http.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{clientId}/statements/balance-sheet?asOf={asOf:yyyy-MM-dd}"))!;
        Assert.True(sheet.IsBalanced,
            $"Balance sheet not balanced as of {asOf}: assets {sheet.TotalAssets} vs L+E {sheet.TotalLiabilitiesAndEquity}");
    }
}
