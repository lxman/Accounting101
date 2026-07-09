using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Receivables.Tests;

/// <summary>Shared setup + assertion helpers for the receivables money/settlement edge-case scenarios,
/// driven end-to-end through the real host.</summary>
internal static class SettlementScenario
{
    internal static async Task SetUpChartAsync(HttpClient controller, Guid clientId, ReceivablesHostFixture f)
    {
        await PutAccountAsync(controller, clientId, f.ReceivableAccountId,      "1100", "Accounts Receivable", "Asset",     null, ["Customer", "Invoice"]);
        await PutAccountAsync(controller, clientId, f.RevenueAccountId,         "4000", "Revenue",             "Revenue",   null);
        await PutAccountAsync(controller, clientId, f.SalesTaxPayableAccountId, "2200", "Sales Tax Payable",   "Liability", null);
        await PutAccountAsync(controller, clientId, f.CashAccountId,            "1000", "Cash",                "Asset",     null);
        await PutAccountAsync(controller, clientId, f.CustomerCreditsAccountId, "2300", "Customer Credits",    "Liability", "Customer");
        await PutAccountAsync(controller, clientId, f.BadDebtExpenseAccountId,  "6000", "Bad Debt Expense",    "Expense",   null);
        await PutAccountAsync(controller, clientId, f.SalesReturnsAccountId,    "4900", "Sales Returns",       "Revenue",   null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension, string[]? requiredDimensions = null)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest
            {
                Number = number, Name = name, Type = type,
                RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions,
            }))
            .EnsureSuccessStatusCode();
    }

    internal static async Task<Guid> CreateCustomerAsync(HttpClient clerk, Guid clientId, string name = "Stark")
    {
        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest(name, null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        return customer.Id;
    }

    internal static async Task<Guid> DraftInvoiceAsync(HttpClient clerk, Guid clientId, Guid customerId, decimal unitPrice)
    {
        DraftInvoiceRequest req = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = unitPrice, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", req))
            .Content.ReadFromJsonAsync<Invoice>())!;
        return draft.Id;
    }

    internal static async Task<Guid> IssueInvoiceAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId,
        decimal unitPrice, decimal taxRate = 0m, bool taxable = false)
    {
        DraftInvoiceRequest req = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = unitPrice, Taxable = taxable }],
            TaxRate: taxRate, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", req))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, issued.Id);
        return issued.Id;
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
