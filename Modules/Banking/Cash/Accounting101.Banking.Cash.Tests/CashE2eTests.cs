using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Cash.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables;
using Accounting101.Payables.Api;
using Accounting101.Payroll;
using Accounting101.Payroll.Api;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// Proves the cash module end-to-end through the real host, with all four modules (Receivables +
/// Payables + Payroll + Cash) installed. A disbursement records and posts a balanced three-line entry
/// stamped <c>ViaModule = "cash"</c> as <c>PendingApproval</c>; a deposit likewise; both are
/// retrievable; void withdraws the entry. The N-module coexistence test enters a Payables bill or
/// Payroll run in the same host and confirms their document stores + entries are not clobbered by
/// Cash's manifest.
/// </summary>
public sealed class CashE2eTests(CashHostFixture fixture) : IClassFixture<CashHostFixture>
{
    private async Task SetUpCashChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,           "1000", "Cash",              "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.InterestExpenseAccountId, "6200", "Interest Expense",  "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.LoanPayableAccountId,     "2500", "Loan Payable",      "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.MembersCapitalAccountId,  "3000", "Members' Capital",  "Equity",    null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Record_a_disbursement_posts_balanced_entry_via_cash_and_voids()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        // Record the cash disbursement — loan payment (Dr Interest 500 + Dr Loan Payable 1500 / Cr Cash 2000).
        RecordCashDisbursementRequest request = new(
            Lines: [
                new CashLineRequest(fixture.InterestExpenseAccountId, 500m),
                new CashLineRequest(fixture.LoanPayableAccountId, 1500m),
            ],
            Date: new DateOnly(2026, 6, 30),
            Reference: "LOAN-2026-06",
            Memo: "Loan payment — interest + principal");

        HttpResponseMessage created = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        CashDisbursement disbursement = (await created.Content.ReadFromJsonAsync<CashDisbursement>())!;
        Assert.Equal(CashDisbursementStatus.Posted, disbursement.Status);

        // Read the resulting engine entry back via sourceRef.
        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disbursement.Id}"))!;
        EntryResponse entry = Assert.Single(entries);

        // Pending approval — the module never approves its own entries.
        Assert.Equal("PendingApproval", entry.Posting);

        // KEY ASSERTION: engine stamped ViaModule = "cash".
        Assert.Equal("cash", entry.ViaModule);

        // Three lines: Dr Interest 500, Dr Loan Payable 1500, Cr Cash 2000.
        Assert.Equal(3, entry.Lines.Count);
        AssertLine(entry, fixture.InterestExpenseAccountId, "Debit",  500m);
        AssertLine(entry, fixture.LoanPayableAccountId,     "Debit",  1500m);
        AssertLine(entry, fixture.CashAccountId,            "Credit", 2000m);

        decimal debits  = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal credits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(2000m, debits);

        // The document is retrievable.
        CashDisbursementView view = (await clerk.GetFromJsonAsync<CashDisbursementView>(
            $"/clients/{clientId}/cash-disbursements/{disbursement.Id}"))!;
        Assert.Equal(disbursement.Id, view.Disbursement.Id);

        // Void withdraws the pending entry — a module document write (cash.write) plus gl.void, so under
        // SoD only the Controller holds both and drives it.
        HttpResponseMessage voided = await controller.PostAsJsonAsync(
            $"/clients/{clientId}/cash-disbursements/{disbursement.Id}/void",
            new Api.VoidReasonRequest("entered in error"));
        voided.EnsureSuccessStatusCode();
        CashDisbursement voidedDoc = (await voided.Content.ReadFromJsonAsync<CashDisbursement>())!;
        Assert.Equal(CashDisbursementStatus.Void, voidedDoc.Status);

        EntryResponse[] afterVoid = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disbursement.Id}"))!;
        Assert.Equal("Voided", Assert.Single(afterVoid).Status);
    }

    [Fact]
    public async Task Record_a_deposit_posts_balanced_entry_via_cash_and_voids()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        // Record the cash deposit — owner contribution (Dr Cash 25000 / Cr Members' Capital 25000).
        RecordCashDepositRequest request = new(
            Lines: [new CashLineRequest(fixture.MembersCapitalAccountId, 25_000m)],
            Date: new DateOnly(2026, 1, 2),
            Reference: "CONTRIB-2026",
            Memo: "Owner contribution");

        HttpResponseMessage created = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        CashDeposit deposit = (await created.Content.ReadFromJsonAsync<CashDeposit>())!;
        Assert.Equal(CashDepositStatus.Posted, deposit.Status);

        // Read the resulting engine entry.
        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={deposit.Id}"))!;
        EntryResponse entry = Assert.Single(entries);

        Assert.Equal("PendingApproval", entry.Posting);

        // KEY ASSERTION: engine stamped ViaModule = "cash".
        Assert.Equal("cash", entry.ViaModule);

        // Two lines: Dr Cash 25000, Cr Members' Capital 25000.
        Assert.Equal(2, entry.Lines.Count);
        AssertLine(entry, fixture.CashAccountId,          "Debit",  25_000m);
        AssertLine(entry, fixture.MembersCapitalAccountId, "Credit", 25_000m);

        decimal debits  = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal credits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(25_000m, debits);

        // The document is retrievable.
        CashDepositView view = (await clerk.GetFromJsonAsync<CashDepositView>(
            $"/clients/{clientId}/cash-deposits/{deposit.Id}"))!;
        Assert.Equal(deposit.Id, view.Deposit.Id);

        // Void — a module document write (cash.write) plus gl.void, so under SoD only the Controller
        // holds both and drives it.
        HttpResponseMessage voided = await controller.PostAsJsonAsync(
            $"/clients/{clientId}/cash-deposits/{deposit.Id}/void",
            new Api.VoidReasonRequest("entered in error"));
        voided.EnsureSuccessStatusCode();
        CashDeposit voidedDoc = (await voided.Content.ReadFromJsonAsync<CashDeposit>())!;
        Assert.Equal(CashDepositStatus.Void, voidedDoc.Status);

        EntryResponse[] afterVoid = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={deposit.Id}"))!;
        Assert.Equal("Voided", Assert.Single(afterVoid).Status);
    }

    /// <summary>
    /// N-module composition proof: in the SAME host that has Cash installed, enter a Payables bill and
    /// confirm it still posts correctly (its document store + engine entry are not clobbered by Cash's
    /// manifest, because each module keys its IDocumentStore by its own module key).
    /// </summary>
    [Fact]
    public async Task Payables_still_posts_alongside_cash_in_the_same_host()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) =
            await fixture.SeedSodClientAsync();

        // Payables chart: A/P (Vendor dimension) + Cash + Rent expense.
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,    "2000", "Accounts Payable", "Liability", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,       "1000", "Cash",             "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId,"5200", "Rent Expense",     "Expense",   null);

        // Create a vendor and enter a bill.
        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("CoexistCo", null)))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Vendor>())!;

        DraftBillRequest draft = new(
            vendor.Id,
            BillDate: new DateOnly(2026, 6, 1),
            DueDate: null,
            VendorReference: "INV-COEXIST",
            Memo: null,
            Lines: [new BillLineBody("June Rent", 1500m, fixture.RentExpenseAccountId)]);

        Bill drafted = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draft))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Bill>())!;

        Bill entered = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{drafted.Id}/enter", null))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Bill>())!;

        // Payables entry posted correctly under its own module, stamped ViaModule = "payables".
        EntryResponse[] billEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={entered.Id}"))!;
        EntryResponse billEntry = Assert.Single(billEntries);
        Assert.Equal("payables", billEntry.ViaModule);
        Assert.Equal("PendingApproval", billEntry.Posting);

        decimal debits  = billEntry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal credits = billEntry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(1500m, debits);

        // Cash also works in the very same host/client — both document stores coexist.
        await SetUpCashChartAsync(controller, clientId);
        RecordCashDisbursementRequest cashRequest = new(
            Lines: [new CashLineRequest(fixture.InterestExpenseAccountId, 500m)],
            Date: new DateOnly(2026, 6, 30),
            Reference: null,
            Memo: null);
        CashDisbursement cashDisb = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements", cashRequest))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<CashDisbursement>())!;

        EntryResponse[] cashEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={cashDisb.Id}"))!;
        Assert.Equal("cash", Assert.Single(cashEntries).ViaModule);
    }

    /// <summary>
    /// Five-module composition proof: Payroll still posts correctly in the same host with Cash installed.
    /// </summary>
    [Fact]
    public async Task Payroll_still_posts_alongside_cash_in_the_same_host()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) =
            await fixture.SeedSodClientAsync();

        // Set up the payroll chart + the shared cash account.
        await PutAccountAsync(controller, clientId, fixture.SalariesExpenseAccountId,     "6000", "Salaries Expense",      "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.PayrollTaxExpenseAccountId,   "6100", "Payroll Tax Expense",   "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,                "1000", "Cash",                  "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.WithholdingsPayableAccountId, "2200", "Withholdings Payable",  "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.PayrollTaxesPayableAccountId, "2300", "Payroll Taxes Payable", "Liability", null);

        RecordPayrollRunRequest runRequest = new(
            Gross: 28000m, EmployeeFica: 2142m, EmployerFica: 2142m,
            Deductions: 0m, IncomeTaxWithheld: 5040m,
            PayDate: new DateOnly(2026, 6, 30), Memo: "June payroll");

        PayrollRun run = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", runRequest))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<PayrollRun>())!;

        EntryResponse[] runEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Id}"))!;
        Assert.Equal("payroll", Assert.Single(runEntries).ViaModule);

        // And Cash also works in the very same host/client.
        await SetUpCashChartAsync(controller, clientId);
        RecordCashDepositRequest cashRequest = new(
            Lines: [new CashLineRequest(fixture.MembersCapitalAccountId, 10_000m)],
            Date: new DateOnly(2026, 1, 2),
            Reference: null,
            Memo: null);
        CashDeposit cashDep = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits", cashRequest))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<CashDeposit>())!;

        EntryResponse[] cashEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={cashDep.Id}"))!;
        Assert.Equal("cash", Assert.Single(cashEntries).ViaModule);
    }

    private static void AssertLine(EntryResponse entry, Guid accountId, string direction, decimal amount)
    {
        EntryLineResponse line = Assert.Single(entry.Lines, l => l.AccountId == accountId);
        Assert.Equal(direction, line.Direction);
        Assert.Equal(amount, line.Amount);
    }
}
