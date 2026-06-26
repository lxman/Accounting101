using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll;

/// <summary>The payroll recipes: a payroll run or a tax remittance each composes into one balanced
/// journal entry. Pure — request in, wire DTO out — leaving sequencing, approval, and persistence
/// to the engine.</summary>
public static class PayrollPosting
{
    public const string PayrollRunSourceType = "PayrollRun";
    public const string TaxRemittanceSourceType = "TaxRemittance";

    /// <summary>Composes the five-line journal entry for a payroll run.
    /// <para>
    /// All five lines are emitted explicitly even when a value is zero (e.g. zero deductions).
    /// This preserves entry shape determinism: downstream readers can always find exactly five lines
    /// indexed by account role without branching on presence. The engine accepts zero-amount lines.
    /// </para>
    /// <para>Throws <see cref="ArgumentException"/> when derived net pay is negative (deductions,
    /// withholdings, and employee FICA exceed gross).</para>
    /// </summary>
    public static PostEntryRequest ComposePayrollRun(Guid runId, PayrollRunBody body, PayrollPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal netPay = body.Gross - body.EmployeeFica - body.IncomeTaxWithheld - body.Deductions;
        if (netPay < 0m)
            throw new ArgumentException(
                "deductions, withholdings, and employee FICA exceed gross",
                nameof(body));

        // Five explicit lines — see XML doc above for the zero-line rationale.
        List<PostLineRequest> lines =
        [
            new(accounts.SalariesExpenseAccountId,   "Debit",  body.Gross),
            new(accounts.PayrollTaxExpenseAccountId, "Debit",  body.EmployerFica),
            new(accounts.CashAccountId,              "Credit", netPay),
            new(accounts.WithholdingsPayableAccountId,  "Credit", body.IncomeTaxWithheld + body.Deductions),
            new(accounts.PayrollTaxesPayableAccountId,  "Credit", body.EmployeeFica + body.EmployerFica),
        ];

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(PayrollRunSourceType, runId),
            EffectiveDate: body.PayDate,
            Reference: null,
            Memo: body.Memo,
            Lines: lines,
            SourceRef: runId,
            SourceType: PayrollRunSourceType);
    }

    /// <summary>Composes the three-line journal entry that pays down the two payroll liabilities.</summary>
    public static PostEntryRequest ComposeTaxRemittance(Guid id, TaxRemittanceBody body, PayrollPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        List<PostLineRequest> lines =
        [
            new(accounts.WithholdingsPayableAccountId,  "Debit",  body.WithholdingsAmount),
            new(accounts.PayrollTaxesPayableAccountId,  "Debit",  body.TaxesAmount),
            new(accounts.CashAccountId,                 "Credit", body.WithholdingsAmount + body.TaxesAmount),
        ];

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(TaxRemittanceSourceType, id),
            EffectiveDate: body.PayDate,
            Reference: null,
            Memo: body.Memo,
            Lines: lines,
            SourceRef: id,
            SourceType: TaxRemittanceSourceType);
    }
}
