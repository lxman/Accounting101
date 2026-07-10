using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables;
using Accounting101.Payables.Api;
using Accounting101.Payroll.Api;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// Proves the payroll module end-to-end through the real host, with all three modules (Receivables +
/// Payables + Payroll) installed. A payroll run records and posts a balanced five-line entry stamped
/// <c>ViaModule = "payroll"</c> as <c>PendingApproval</c>; the run document is retrievable; void
/// withdraws the entry. The N-module coexistence test enters a Payables bill in the same host and
/// confirms its document store + entry are not clobbered by Payroll's manifest.
/// </summary>
public sealed class PayrollE2eTests(PayrollHostFixture fixture) : IClassFixture<PayrollHostFixture>
{
    // The dataset numbers from the spec.
    private const decimal Gross = 28000m;
    private const decimal EmployeeFica = 2142m;
    private const decimal EmployerFica = 2142m;
    private const decimal Deductions = 0m;
    private const decimal IncomeTax = 5040m;

    // The derived posting amounts.
    private const decimal NetPay = 20818m;            // 28000 - 2142 - 5040 - 0
    private const decimal WithholdingsCredit = 5040m; // 5040 + 0
    private const decimal PayrollTaxesCredit = 4284m; // 2142 + 2142

    private async Task SetUpPayrollChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.SalariesExpenseAccountId,     "6000", "Salaries Expense",      "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.PayrollTaxExpenseAccountId,   "6100", "Payroll Tax Expense",   "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,                "1000", "Cash",                  "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.WithholdingsPayableAccountId, "2200", "Withholdings Payable",  "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.PayrollTaxesPayableAccountId, "2300", "Payroll Taxes Payable", "Liability", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Record_a_payroll_run_posts_a_balanced_five_line_entry_via_payroll_and_voids()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        // Record + post the payroll run.
        RecordPayrollRunRequest request = new(
            Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax,
            PayDate: new DateOnly(2026, 6, 30), Memo: "June payroll");

        HttpResponseMessage created = await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        PayrollRun run = (await created.Content.ReadFromJsonAsync<PayrollRun>())!;
        Assert.Equal(PayrollRunStatus.Posted, run.Status);

        // Read the resulting engine entry back via sourceRef.
        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Id}"))!;
        EntryResponse entry = Assert.Single(entries);

        // It is pending approval — the module never approves its own entries.
        Assert.Equal("PendingApproval", entry.Posting);

        // KEY ASSERTION: the engine stamped ViaModule = "payroll" from the X-Module-* headers.
        Assert.Equal("payroll", entry.ViaModule);

        // Five lines, balanced, hitting the five configured accounts with the right amounts.
        Assert.Equal(5, entry.Lines.Count);
        AssertLine(entry, fixture.SalariesExpenseAccountId,     "Debit",  Gross);
        AssertLine(entry, fixture.PayrollTaxExpenseAccountId,   "Debit",  EmployerFica);
        AssertLine(entry, fixture.CashAccountId,                "Credit", NetPay);
        AssertLine(entry, fixture.WithholdingsPayableAccountId, "Credit", WithholdingsCredit);
        AssertLine(entry, fixture.PayrollTaxesPayableAccountId, "Credit", PayrollTaxesCredit);

        decimal debits = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal credits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(30142m, debits);

        // The run document is retrievable.
        PayrollRunView view = (await clerk.GetFromJsonAsync<PayrollRunView>(
            $"/clients/{clientId}/payroll-runs/{run.Id}"))!;
        Assert.Equal(run.Id, view.Run.Id);
        Assert.Equal(Gross, view.Run.Gross);

        // Void withdraws the pending entry and marks the doc Void. Voiding a module document requires the
        // module's .write capability plus gl.void (for a pending entry) — only the Controller holds both
        // under SoD, so the void must be driven by the controller.
        HttpResponseMessage voided = await controller.PostAsJsonAsync(
            $"/clients/{clientId}/payroll-runs/{run.Id}/void", new Api.VoidReasonRequest("entered in error"));
        voided.EnsureSuccessStatusCode();
        PayrollRun voidedRun = (await voided.Content.ReadFromJsonAsync<PayrollRun>())!;
        Assert.Equal(PayrollRunStatus.Void, voidedRun.Status);

        EntryResponse[] afterVoid = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Id}"))!;
        Assert.Equal("Voided", Assert.Single(afterVoid).Status);
    }

    [Fact]
    public async Task A_tax_remittance_pays_down_the_two_liabilities_via_payroll()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) =
            await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        RecordTaxRemittanceRequest request = new(
            WithholdingsAmount: WithholdingsCredit, TaxesAmount: PayrollTaxesCredit,
            PayDate: new DateOnly(2026, 7, 15), Memo: "Q2 remittance");

        HttpResponseMessage created = await clerk.PostAsJsonAsync($"/clients/{clientId}/tax-remittances", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        TaxRemittance remittance = (await created.Content.ReadFromJsonAsync<TaxRemittance>())!;

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={remittance.Id}"))!;
        EntryResponse entry = Assert.Single(entries);

        Assert.Equal("payroll", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        Assert.Equal(3, entry.Lines.Count);
        AssertLine(entry, fixture.WithholdingsPayableAccountId, "Debit",  WithholdingsCredit);
        AssertLine(entry, fixture.PayrollTaxesPayableAccountId, "Debit",  PayrollTaxesCredit);
        AssertLine(entry, fixture.CashAccountId,                "Credit", WithholdingsCredit + PayrollTaxesCredit);
    }

    /// <summary>
    /// N-module composition proof: in the SAME host that has Payroll installed, enter a Payables bill
    /// and confirm it still posts correctly (its document store + engine entry are not clobbered by
    /// Payroll's manifest, because each module keys its IDocumentStore by its own module key).
    /// </summary>
    [Fact]
    public async Task Payables_still_posts_alongside_payroll_in_the_same_host()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) =
            await fixture.SeedSodClientAsync();

        // Payables chart: A/P (Vendor dimension) + Cash + an expense account.
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,   "2000", "Accounts Payable", "Liability", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,      "1000", "Cash",             "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId,"5200", "Rent Expense",     "Expense",   null);

        // Create a vendor and draft + enter a bill.
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

        // The Payables entry posted correctly under its own module, stamped ViaModule = "payables".
        EntryResponse[] billEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={entered.Id}"))!;
        EntryResponse billEntry = Assert.Single(billEntries);
        Assert.Equal("payables", billEntry.ViaModule);
        Assert.Equal("PendingApproval", billEntry.Posting);

        decimal debits = billEntry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal credits = billEntry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(1500m, debits);

        // And Payroll still works in the very same host/client — both document stores coexist.
        await SetUpPayrollChartAsync(controller, clientId);
        RecordPayrollRunRequest runRequest = new(
            Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax,
            PayDate: new DateOnly(2026, 6, 30), Memo: null);
        PayrollRun run = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", runRequest))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<PayrollRun>())!;

        EntryResponse[] runEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Id}"))!;
        Assert.Equal("payroll", Assert.Single(runEntries).ViaModule);
    }

    [Fact]
    public async Task Run_list_reflects_module_void_across_the_page()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        RecordPayrollRunRequest r1 = new(Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax, new DateOnly(2026, 6, 30), null);
        RecordPayrollRunRequest r2 = new(Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax, new DateOnly(2026, 7, 31), null);

        PayrollRun run1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", r1)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PayrollRun>())!;
        PayrollRun run2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", r2)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PayrollRun>())!;

        // Void the second via the module (Controller holds payroll.write + gl.void under SoD).
        (await controller.PostAsJsonAsync($"/clients/{clientId}/payroll-runs/{run2.Id}/void", new Api.VoidReasonRequest("error"))).EnsureSuccessStatusCode();

        PagedResponse<PayrollRunView> page = (await clerk.GetFromJsonAsync<PagedResponse<PayrollRunView>>(
            $"/clients/{clientId}/payroll-runs?includeVoided=true&order=asc"))!;

        Assert.Equal(PayrollRunStatus.Posted, page.Items.Single(v => v.Run.Id == run1.Id).Run.Status);
        Assert.Equal(PayrollRunStatus.Void, page.Items.Single(v => v.Run.Id == run2.Id).Run.Status);
    }

    [Fact]
    public async Task Raw_gl_reverse_of_a_payroll_entry_is_refused_by_the_guard()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpPayrollChartAsync(controller, clientId);

        RecordPayrollRunRequest request = new(Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax, new DateOnly(2026, 6, 30), null);
        PayrollRun run = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", request)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PayrollRun>())!;

        // Find and approve the spawned entry, so a reversal (not a withdrawal) is what a raw caller would attempt.
        EntryResponse entry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={run.Id}"))!);
        (await approver.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

        // Raw reverse — a plain user request carrying no module credential — is refused: correct it through the module.
        HttpResponseMessage rawReverse = await controller.PostAsJsonAsync(
            $"/clients/{clientId}/entries/{entry.Id}/reverse",
            new ReverseRequest(new DateOnly(2026, 7, 1), "raw reversal attempt"));
        Assert.Equal(HttpStatusCode.Conflict, rawReverse.StatusCode);
    }

    private static void AssertLine(EntryResponse entry, Guid accountId, string direction, decimal amount)
    {
        EntryLineResponse line = Assert.Single(entry.Lines, l => l.AccountId == accountId);
        Assert.Equal(direction, line.Direction);
        Assert.Equal(amount, line.Amount);
    }
}
