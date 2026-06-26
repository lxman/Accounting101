using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll.Tests;

public sealed class PayrollPostingTests
{
    // Helper: signed amount — debits positive, credits negative — for balance checks.
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    private static PayrollPostingAccounts MakeAccounts(out Guid salaries, out Guid payrollTaxExp,
        out Guid cash, out Guid withholdings, out Guid payrollTaxesPayable)
    {
        salaries = Guid.NewGuid();
        payrollTaxExp = Guid.NewGuid();
        cash = Guid.NewGuid();
        withholdings = Guid.NewGuid();
        payrollTaxesPayable = Guid.NewGuid();
        return new PayrollPostingAccounts
        {
            SalariesExpenseAccountId = salaries,
            PayrollTaxExpenseAccountId = payrollTaxExp,
            CashAccountId = cash,
            WithholdingsPayableAccountId = withholdings,
            PayrollTaxesPayableAccountId = payrollTaxesPayable,
        };
    }

    // Test 1: standard payroll run with the spec dataset — exact line amounts and balanced.
    [Fact]
    public void ComposePayrollRun_standard_dataset_produces_five_lines_and_balances()
    {
        Guid runId = Guid.NewGuid();
        PayrollPostingAccounts accounts = MakeAccounts(
            out Guid salaries, out Guid payrollTaxExp, out Guid cash,
            out Guid withholdings, out Guid payrollTaxesPayable);

        PayrollRunBody body = new(
            Gross: 28_000m,
            EmployeeFica: 2_142m,
            EmployerFica: 2_142m,
            Deductions: 0m,
            IncomeTaxWithheld: 5_040m,
            PayDate: new DateOnly(2026, 6, 30),
            Memo: null);

        PostEntryRequest entry = PayrollPosting.ComposePayrollRun(runId, body, accounts);

        Assert.Equal(5, entry.Lines.Count);

        PostLineRequest salariesLine = entry.Lines.Single(l => l.AccountId == salaries);
        Assert.Equal("Debit",  salariesLine.Direction);
        Assert.Equal(28_000m,  salariesLine.Amount);

        PostLineRequest taxExpLine = entry.Lines.Single(l => l.AccountId == payrollTaxExp);
        Assert.Equal("Debit", taxExpLine.Direction);
        Assert.Equal(2_142m,  taxExpLine.Amount);

        PostLineRequest cashLine = entry.Lines.Single(l => l.AccountId == cash);
        Assert.Equal("Credit", cashLine.Direction);
        Assert.Equal(20_818m,  cashLine.Amount); // 28000 - 2142 - 5040 - 0

        PostLineRequest withholdingsLine = entry.Lines.Single(l => l.AccountId == withholdings);
        Assert.Equal("Credit", withholdingsLine.Direction);
        Assert.Equal(5_040m,   withholdingsLine.Amount); // incomeTax + deductions

        PostLineRequest taxesPayableLine = entry.Lines.Single(l => l.AccountId == payrollTaxesPayable);
        Assert.Equal("Credit", taxesPayableLine.Direction);
        Assert.Equal(4_284m,   taxesPayableLine.Amount); // empFICA + emprFICA

        Assert.Equal(0m, entry.Lines.Sum(Signed)); // balanced
    }

    // Test 2: non-zero deductions reduce net-pay (Cash) and increase Withholdings Payable.
    [Fact]
    public void ComposePayrollRun_with_deductions_reduces_cash_and_increases_withholdings()
    {
        Guid runId = Guid.NewGuid();
        PayrollPostingAccounts accounts = MakeAccounts(
            out _, out _, out Guid cash, out Guid withholdings, out _);

        PayrollRunBody body = new(
            Gross: 28_000m,
            EmployeeFica: 2_142m,
            EmployerFica: 2_142m,
            Deductions: 500m,
            IncomeTaxWithheld: 5_040m,
            PayDate: new DateOnly(2026, 6, 30),
            Memo: null);

        PostEntryRequest entry = PayrollPosting.ComposePayrollRun(runId, body, accounts);

        Assert.Equal(20_318m, entry.Lines.Single(l => l.AccountId == cash).Amount);       // 28000-2142-5040-500
        Assert.Equal(5_540m,  entry.Lines.Single(l => l.AccountId == withholdings).Amount); // 5040+500
        Assert.Equal(0m, entry.Lines.Sum(Signed)); // still balanced
    }

    // Test 3: negative net pay → ArgumentException.
    [Fact]
    public void ComposePayrollRun_negative_net_pay_throws_ArgumentException()
    {
        Guid runId = Guid.NewGuid();
        PayrollPostingAccounts accounts = MakeAccounts(out _, out _, out _, out _, out _);

        PayrollRunBody body = new(
            Gross: 1_000m,
            EmployeeFica: 0m,
            EmployerFica: 0m,
            Deductions: 2_000m,
            IncomeTaxWithheld: 0m,
            PayDate: new DateOnly(2026, 6, 30),
            Memo: null);

        Assert.Throws<ArgumentException>(() =>
            PayrollPosting.ComposePayrollRun(runId, body, accounts));
    }

    // Test 4: tax remittance — Dr Withholdings, Dr PayrollTaxesPayable, Cr Cash; balanced.
    [Fact]
    public void ComposeTaxRemittance_debits_liabilities_and_credits_cash_balanced()
    {
        Guid id = Guid.NewGuid();
        PayrollPostingAccounts accounts = MakeAccounts(
            out _, out _, out Guid cash, out Guid withholdings, out Guid payrollTaxesPayable);

        TaxRemittanceBody body = new(
            WithholdingsAmount: 5_040m,
            TaxesAmount: 4_284m,
            PayDate: new DateOnly(2026, 7, 15),
            Memo: null);

        PostEntryRequest entry = PayrollPosting.ComposeTaxRemittance(id, body, accounts);

        PostLineRequest withholdingsLine = entry.Lines.Single(l => l.AccountId == withholdings);
        Assert.Equal("Debit",  withholdingsLine.Direction);
        Assert.Equal(5_040m,   withholdingsLine.Amount);

        PostLineRequest taxesLine = entry.Lines.Single(l => l.AccountId == payrollTaxesPayable);
        Assert.Equal("Debit", taxesLine.Direction);
        Assert.Equal(4_284m,  taxesLine.Amount);

        PostLineRequest cashLine = entry.Lines.Single(l => l.AccountId == cash);
        Assert.Equal("Credit", cashLine.Direction);
        Assert.Equal(9_324m,   cashLine.Amount); // 5040 + 4284

        Assert.Equal(0m, entry.Lines.Sum(Signed)); // balanced
    }

    // Test 5a: ComposePayrollRun carries SourceType="PayrollRun" and a deterministic Id.
    [Fact]
    public void ComposePayrollRun_carries_correct_SourceType_and_deterministic_Id()
    {
        Guid runId = Guid.NewGuid();
        PayrollPostingAccounts accounts = MakeAccounts(out _, out _, out _, out _, out _);
        PayrollRunBody body = new(28_000m, 2_142m, 2_142m, 0m, 5_040m, new DateOnly(2026, 6, 30), null);

        PostEntryRequest a = PayrollPosting.ComposePayrollRun(runId, body, accounts);
        PostEntryRequest b = PayrollPosting.ComposePayrollRun(runId, body, accounts);

        Assert.Equal("PayrollRun", a.SourceType);
        Assert.Equal(runId, a.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(PayrollPosting.PayrollRunSourceType, runId), a.Id);
        Assert.Equal(a.Id, b.Id); // deterministic across calls
    }

    // Test 5b: ComposeTaxRemittance carries SourceType="TaxRemittance" and a deterministic Id.
    [Fact]
    public void ComposeTaxRemittance_carries_correct_SourceType_and_deterministic_Id()
    {
        Guid id = Guid.NewGuid();
        PayrollPostingAccounts accounts = MakeAccounts(out _, out _, out _, out _, out _);
        TaxRemittanceBody body = new(5_040m, 4_284m, new DateOnly(2026, 7, 15), null);

        PostEntryRequest a = PayrollPosting.ComposeTaxRemittance(id, body, accounts);
        PostEntryRequest b = PayrollPosting.ComposeTaxRemittance(id, body, accounts);

        Assert.Equal("TaxRemittance", a.SourceType);
        Assert.Equal(id, a.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(PayrollPosting.TaxRemittanceSourceType, id), a.Id);
        Assert.Equal(a.Id, b.Id); // deterministic across calls
    }
}
