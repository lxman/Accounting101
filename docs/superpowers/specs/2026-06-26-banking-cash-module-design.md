# Slice 3 — Banking/Cash module (POC) — Design

**Date:** 2026-06-26
**Status:** Spec for review
**Umbrella:** [MVP Module Architecture](2026-06-26-mvp-module-architecture-design.md) — build-sequence slice 3 (the Cash half of Cash/Banking).

## Goal & layout

A new **Cash** module for direct bank-account movements that have **no trade document**: the clerk records a **cash disbursement** (loan payments, insurance prepay, income-tax payments, owner draws) or a **cash deposit** (owner contributions, loan proceeds, other non-invoice cash in). The module derives the balanced entry and posts it under its own `cash` credential (`viaModule="cash"`). Translation only — POC.

**Folder layout** (new `Modules/Banking/` grouping folder, two sibling modules):
- `Modules/Banking/Cash/Accounting101.Banking.Cash` (+ `.Api`, `.Tests`) — **built this slice**.
- `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation` — **scaffold only** (empty project added to the solution; bank reconciliation is a later slice, deliberately deferred — it's a matching/clearing feature, not a posting cycle, and not on the clerk-through-modules critical path).

Banking is just the grouping folder; **Cash and Reconciliation are independent engine modules** (each its own `ModuleIdentity`, credential, manifest). This slice = Cash, mirroring Payables/Payroll. It is the **fifth** installed module (Receivables, Payables, Payroll, Cash) — further N-module composition coverage.

## The recipe (the heart)

A voucher is a *bill without a vendor that hits Cash directly*. The clerk supplies the **non-cash** lines (account + amount); the configured **Cash** account is the other side.

### Cash disbursement (`Dr lines / Cr Cash`)
```
CashDisbursement(lines: [(accountId, amount)...], date, reference?, memo?):
  Dr  each line's account = amount   (lines sharing an account collapse; ordered by account id for determinism)
  Cr  Cash                = Σ amounts
```
e.g. loan payment → `Dr Interest Expense + Dr Loan Payable / Cr Cash`; insurance prepay → `Dr Prepaid Insurance / Cr Cash`; income-tax payment → `Dr Income Tax Payable / Cr Cash`; owner draw → `Dr Owner Draws / Cr Cash`.

### Cash deposit (`Dr Cash / Cr lines`) — symmetric
```
CashDeposit(lines, date, reference?, memo?):
  Dr  Cash               = Σ amounts
  Cr  each line's account = amount
```
e.g. owner contribution → `Dr Cash / Cr Members' Capital`; loan proceeds → `Dr Cash / Cr Loan Payable`.

### Configured accounts (`CashPostingAccounts`)
One configured `CashAccountId` (the default bank account). The line accounts are clerk-supplied per voucher (like a bill's expense accounts). **Multiple bank accounts** (and thus account-to-account transfers) are a later concern — they arrive naturally with bank reconciliation; one cash account covers every dataset cycle for the POC.

A voucher with an empty line list, a non-positive amount, or (defensively) a Cash account appearing in the lines is rejected.

## Documents & lifecycle
- `CashDisbursement` and `CashDeposit` are evidentiary documents (engine document store), auditable + voidable.
- **One-step record-and-post** (no draft): recording composes + posts the entry `PendingApproval` (module never approves — SoD) and persists the document; **void** reverses (posted) / withdraws (pending) by `SourceRef`. Mirrors Payables bill-payment / Payroll.
- Lines are `(accountId, amount)` — **no dimension** for the POC (the dataset's cash cycles carry none; the engine dimension bag makes adding one later non-breaking).

## Module identity, endpoints
`AddCash`: manifest (evidentiary `cash-disbursements`, `cash-deposits`), `ModuleIdentity("cash")` + credential (slice 1), `ConfiguredCashAccountsProvider` (config `Cash:Accounts:Cash` → the cash account id), the typed `HttpLedgerClient` posting under the `cash` credential. Host wires `AddCash` + `MapCashEndpoints`.
Endpoints: `POST /clients/{clientId}/cash-disbursements` (+ `/{id}/void`), `POST .../cash-deposits` (+ `/{id}/void`), `GET` single + list for each.

## Out of scope (named)
Bank reconciliation (next slice), multiple bank accounts / inter-account transfers, statement import, cleared/uncleared tracking, dimensions on cash lines, pagination on lists.

## Testing
- **Recipe (pure):** a disbursement composes `Dr lines / Cr Cash` balanced (lines collapse/order deterministically); a deposit composes the inverse; empty/zero/negative rejected. Loan-payment case (`Dr Interest 500 + Dr Loan Payable 1500 / Cr Cash 2000`) and owner-contribution deposit verified.
- **Service (vs fake `ILedgerClient`):** record persists the document + posts `PendingApproval`; void reverses.
- **Cross-host integration (N-module proof):** boot the host with all modules (Receivables + Payables + Payroll + **Cash**); record a disbursement over HTTP → engine entry balanced, hits the right accounts, status `PendingApproval`, **`viaModule="cash"`**; a deposit likewise; void works; and a Payables/Payroll operation still works alongside (no manifest clobber with a fifth module).

## Reconciliation scaffold
Create `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/Accounting101.Banking.Reconciliation.csproj` as an empty class library (one placeholder file, e.g. a doc-comment marker class noting "Reconciliation module — implemented in a later slice"), added to `Accounting101.slnx`. No registration, no host wiring, no behavior. It exists only to establish the layout.

## Global constraints
- .NET 10; build 0 warnings; commit per task; TDD; EphemeralMongo.
- Module never approves its own entries (`PendingApproval`); posts under the `cash` credential → `viaModule="cash"`.
- Configured cash account id (no hardcoded numbers). Mirror Payables/Payroll conventions (layout, naming, configured-accounts provider, keyed credential, manifest).
- Namespaces follow folder: `Accounting101.Banking.Cash`, module key `"cash"`.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
