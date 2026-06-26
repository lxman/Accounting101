# Slice 2 — Payroll module (POC) — Design

**Date:** 2026-06-26
**Status:** Spec for review
**Umbrella:** [MVP Module Architecture](2026-06-26-mvp-module-architecture-design.md) — build-sequence slice 2.

## Goal & scope

A new **Payroll** module: the clerk records a **payroll run** and a **tax remittance** as business documents; the module derives the balanced journal entry and posts it under its own `payroll` credential (slice 1). **Translation only** — the amounts arrive pre-computed; no withholding tables, no employee entity, no time tracking, no filings. POC to plug the GL hole and demo the module pattern; expandable into a real payroll system later.

Structurally a straight mirror of the **Payables** module (recipe → balanced entry, document store, service, configured accounts, `ILedgerClient`/`HttpLedgerClient`, `AddPayroll` registration). It is the **third** installed module, so it is the live test of N-module host composition.

## The recipe (the heart)

### Payroll run
Clerk enters five numbers: `gross`, `employeeFica`, `employerFica`, `deductions`, `incomeTaxWithheld` (+ `payDate`, optional `memo`). The module composes:

```
Dr  Salaries Expense        = gross
Dr  Payroll Tax Expense     = employerFica
  Cr Cash (net pay)         = gross − employeeFica − incomeTaxWithheld − deductions
  Cr Withholdings Payable   = incomeTaxWithheld + deductions      (deductions lump here — POC simplification)
  Cr Payroll Taxes Payable  = employeeFica + employerFica
```

Validated against the dataset (gross 28,000; empFICA 2,142; emprFICA 2,142; incomeTax 5,040; deductions 0) → Dr 30,142 = Cr 20,818 + 5,040 + 4,284. Balances. The module rejects a run whose derived net pay is negative (deductions + withholdings > gross).

### Tax remittance
Clerk enters `withholdingsAmount`, `taxesAmount` (+ `payDate`, optional `memo`). The module composes:
```
Dr Withholdings Payable   = withholdingsAmount
Dr Payroll Taxes Payable  = taxesAmount
  Cr Cash                 = withholdingsAmount + taxesAmount
```
(Pays down the two liabilities the run created. The module does not track outstanding balances — the clerk supplies the amounts; correctness of *which* liability is owed is the accountant's/reconciler's concern, as today.)

### Configured accounts (`PayrollPostingAccounts`)
Five account IDs, supplied by configuration (mirrors `BillPostingAccounts` + `ConfiguredBillAccountsProvider`): `SalariesExpenseAccountId`, `PayrollTaxExpenseAccountId`, `CashAccountId`, `WithholdingsPayableAccountId`, `PayrollTaxesPayableAccountId`. No hardcoded numbers.

## Documents & lifecycle

- **`PayrollRun`** and **`TaxRemittance`** are evidentiary documents (engine document store, like bills) — auditable and voidable.
- **One-step record-and-post** (no draft): recording the document composes + posts the entry as `PendingApproval` (the module never approves — SoD), and persists the document. **Void** reverses it (mirrors `BillPayment`/the payment pattern). A payroll run is an event you run, not a scratch doc you build up.
- **No employee entity, no dimension** — summary-level entry. (Deliberately omitted; the engine's dimension bag means adding an `Employee` dimension later is non-breaking.)

## Module identity & posting

`AddPayroll` registers: the collection manifest (`payroll-runs`, `tax-remittances` evidentiary), the `payroll` `ModuleIdentity` + its credential (slice 1), and the keyed `ModuleCredential`. The Payroll `HttpLedgerClient` posts under the `payroll` credential (`[FromKeyedServices("payroll")]`), so its entries are stamped `viaModule="payroll"`. Approve/void forward the user token (not new-entry origination), per slice 1.

## Endpoints (`MapPayrollEndpoints`)
- `POST /clients/{clientId}/payroll-runs` — record + post a payroll run → returns the run (with the posted entry id).
- `POST /clients/{clientId}/payroll-runs/{id}/void` — void it.
- `POST /clients/{clientId}/tax-remittances` — record + post a remittance.
- `POST /clients/{clientId}/tax-remittances/{id}/void`.
- `GET` single + list for each (thin, mirrors Payables reads).

## Out of scope (named so they don't creep)
Withholding/tax computation, employee records, pay schedules, time tracking, per-employee lines, garnishment/benefit splits (deductions lump into Withholdings Payable), multi-state, filings (941/W-2). Net-pay-as-derived is the only arithmetic.

## Testing
- **Recipe (pure, no host):** the run composes the exact 5-line entry for the dataset numbers; deductions reduce net pay and add to Withholdings Payable; negative net pay is rejected; the remittance composes `Dr/Dr/Cr Cash`. Balanced in every case.
- **Service (vs a fake `ILedgerClient`, like Payables):** record persists the document + posts `PendingApproval`; void reverses.
- **Cross-host integration (the N-module proof):** boot the host with **all three** modules (Receivables + Payables + Payroll); record a payroll run over HTTP; assert the engine entry is balanced, hits the five configured accounts with the right amounts, and is stamped **`viaModule="payroll"`**; and confirm Receivables + Payables still work (their manifests/doc-stores aren't clobbered by the third module).

## Global constraints
- .NET 10; build 0 warnings; commit per task; TDD; EphemeralMongo.
- Module never approves its own entries (post `PendingApproval`).
- Mirror Payables conventions (file layout, naming, configured-accounts provider, keyed credential).
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
