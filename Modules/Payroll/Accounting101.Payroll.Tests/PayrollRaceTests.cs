using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THESE VALID: revert the service's <c>replayed = !result.Created;</c> to
/// <c>replayed = known is not null;</c> and both racers claim a create, so the single-create
/// assertion sees two and fails.
/// </summary>
public sealed class PayrollRaceTests
{
    private static readonly PayrollPostingAccounts Accounts = new()
    {
        SalariesExpenseAccountId    = Guid.NewGuid(),
        PayrollTaxExpenseAccountId  = Guid.NewGuid(),
        CashAccountId               = Guid.NewGuid(),
        WithholdingsPayableAccountId = Guid.NewGuid(),
        PayrollTaxesPayableAccountId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Two_run_racers_past_the_probe_yield_exactly_one_create()
    {
        // Build clientId, the accounts provider and FakeLedgerClient exactly as PayrollServiceTests
        // does (the file that actually constructs a bare PayrollService with fakes — PayrollRetryKeyTests
        // is HTTP/fixture-based and never does), then substitute the racing store into the constructor.
        InMemoryPayrollRunStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPayrollRunStore racingStore = new(inner, rendezvous);
        InMemoryTaxRemittanceStore remittanceStore = new();
        FakeLedgerClient ledger = new();
        PayrollService service = new(racingStore, remittanceStore, new FixedPayrollAccountsProvider(Accounts), ledger);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        PayrollRunBody body = new(
            Gross: 10_000m, EmployeeFica: 620m, EmployerFica: 620m,
            Deductions: 200m, IncomeTaxWithheld: 1_500m,
            PayDate: new DateOnly(2026, 6, 30), Memo: "June payroll");

        Task<(PayrollRun Run, bool Replayed)> first = service.RecordRunAsync(clientId, body, retryKey);
        Task<(PayrollRun Run, bool Replayed)> second = service.RecordRunAsync(clientId, body, retryKey);
        (PayrollRun Run, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Run.Id);
    }

    [Fact]
    public async Task Two_remittance_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryTaxRemittanceStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousTaxRemittanceStore racingStore = new(inner, rendezvous);
        InMemoryPayrollRunStore runStore = new();
        FakeLedgerClient ledger = new();
        PayrollService service = new(runStore, racingStore, new FixedPayrollAccountsProvider(Accounts), ledger);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        TaxRemittanceBody body = new(
            WithholdingsAmount: 1_700m, TaxesAmount: 1_240m,
            PayDate: new DateOnly(2026, 7, 15), Memo: "Q2 remittance");

        Task<(TaxRemittance Remittance, bool Replayed)> first = service.RecordRemittanceAsync(clientId, body, retryKey);
        Task<(TaxRemittance Remittance, bool Replayed)> second = service.RecordRemittanceAsync(clientId, body, retryKey);
        (TaxRemittance Remittance, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Remittance.Id);
    }
}
