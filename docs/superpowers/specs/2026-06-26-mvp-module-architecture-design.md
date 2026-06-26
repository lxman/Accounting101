# MVP Module Architecture — clerks work through modules, engine stays the spine — Design

**Date:** 2026-06-26
**Status:** Spec for review (scope/strategy doc; each slice below gets its own design + plan)

## Purpose

Define the **MVP module boundary** so the product has a clear, finite scope, and redesign authorization so **A/R and A/P clerks work only through modules — never raw GL** (`POST /entries`). This doc is the umbrella; the numbered slices each get their own spec → plan → build cycle.

## Principle — the engine is the spine, modules are translators

The engine (GL + the three statements + audit chain + period close + reconciliation) is **done and hardened (~5,900 LOC)**. It is the single source of truth: the journal is the only state; every report is derived by replay. A **module** is a thin translator (~1,000 LOC) that turns one kind of **business document** into a balanced journal entry and hands it to the engine. Modules never re-implement accounting.

A capability earns **MVP module** status by one test: **a clerk needs it to enter that cycle without touching raw GL, and the dog-food dataset exercises it.** Accountant *adjustments* (accruals, depreciation, prepaid amortization, tax provisioning) stay **raw GL** — no module.

## The four MVP modules (bounded by document type)

| Module | Owns (business document) | Status |
|---|---|---|
| **Receivables** | customer invoices, receipts / cash application | exists; finish cash-application |
| **Payables** | **vendor** bills + payments (trade only) | exists |
| **Payroll** | payroll runs + payroll-tax remittance | **new** |
| **Cash / Banking** | bank-account movements with **no trade document** — loan payments, insurance prepay, transfers, owner draws/contributions — **plus bank reconciliation** | **new** |

Each module owns exactly one document family, non-overlapping: customer docs / vendor docs / payroll docs / bank-account docs.

### Stays raw GL (accountant / controller — not a module)
Adjusting entries: accruals, **depreciation**, prepaid amortization, tax provisioning. The general journal is the accountant's desk.

### Explicitly OUT of MVP (future modules, named so they don't creep in)
- **Fixed Assets** register + auto-depreciation (depreciation stays a manual adjusting JE).
- **Inventory / COGS** (services business — not exercised).
- **Tax** automation (sales/income tax stays a manual JE).
- Multi-currency (already decided: USD-only), budgeting, POs/SOs, time tracking.

## Key scoping decisions

1. **Payroll = translation, not a tax engine.** The module owns the *journal translation* only: it derives the balanced entry from `gross / withholdings / employer-tax` inputs that arrive **already computed** (by the clerk, or eventually an external payroll service). The withholding tables, multi-state rules, garnishments, and regulatory filings (941/W-2) — the part that makes commercial payroll enormous — are **explicitly not in scope.** This is why a payroll *module* is ~1k LOC, not 250k.
2. **Non-trade disbursements live in Cash/Banking, not Payables.** Loan payments, insurance prepay, and general coded cash-out have no vendor bill — they're just cash leaving the bank. Putting them in Cash/Banking (alongside reconciliation) keeps **Payables strictly trade** and gives Cash/Banking a crisp job. (This supersedes the earlier "extend Payables with a disbursement voucher" idea.)
3. **A module is worth building (vs raw GL) because it gives three things raw `POST /entries` cannot:** controlled transaction types (the clerk can't post arbitrary lines), a **source document** (auditable, reconcilable, voidable), and posting under the **module's own identity** (so the clerk needs no raw `Post`).

## Authorization redesign — clerks restricted to modules

**Why:** today the `Clerk` role holds `Permission.Post` (raw GL), and the modules post by **forwarding the clerk's token** — so the same permission that lets the module post a bill also lets the clerk call `POST /entries` directly and bypass the module (the sim caught exactly this: an AP clerk posting bills as plain JEs with `sourceType: null`). That conflates "create a business document" with "post arbitrary GL." Least privilege says split them. Policy lives **upstream in the host** (the engine stays policy-light); the engine already has `ModuleIdentity` + `IModuleAuthenticator` + `ModuleAccess` (used today for document-store scoping) to build on.

**Target model:**
- **Module posts under its own identity** (not the forwarded clerk token), while **stamping the originating user (clerk) as the business actor** for the audit trail / SoD. Authorization principal = module; recorded actor = clerk.
- **Clerk role loses raw `Post`** → clerk = module capabilities + `Read`. A direct `POST /entries` by a clerk → `403`.
- **Controller / Accountant keep raw `Post`** for legitimate adjustments. (Real-shop SoD: a staff accountant *makes* GL entries, the controller *approves* — maker ≠ checker.)

## Build sequence (dependency-ordered; each is its own slice → spec → plan → SDD)

1. **Module-poster identity + actor stamping** *(foundation)* — the engine accepts a post authenticated as a module identity, authorizes the module, records the clerk as actor. Existing modules keep working; clerk keeps `Post` for now. Nothing else can land cleanly before this.
2. **Payroll module** — payroll run + tax remittance documents (translation only).
3. **Cash / Banking module** — bank-account register, non-trade disbursements (loans, insurance, transfers, draws), bank reconciliation.
4. **Receivables cash-application finish** + route **receipts through the Receivables payment module** (drop raw-JE receipts; exercises the settlement guard).
5. **Migrate Receivables + Payables** to post under module identity (off the forwarded clerk token).
6. **Remove raw `Post` from the Clerk role** — now every clerk cycle has a module path; rework SoD (controller approves).
7. **Sim role briefs + dataset cycle-mapping** updated to match; re-run the 2-year dog-food on the corrected model.

> Slices 2 and 3 (the new modules) can proceed in parallel once slice 1 lands. Slice 6 is gated on 2–5 (the clerk can't lose `Post` until every cycle it does has a module path).

## Cross-cutting check (verify early)
The host historically had a "one module per host" wrinkle (a second `AddModule` could clobber the first's document-store manifest). Receivables + Payables coexist today, but **Payroll makes it three** — confirm the manifest registration genuinely composes N modules before slice 2.

## Testing / validation
Each module is unit-tested (EphemeralMongo, TDD) and then **dog-fooded** end-to-end through the simulation (the harness derives work-queues from the dataset; clerks drive the modules over HTTP; the reconciler + auditor verify ledger integrity). The 2-year sim on the corrected role model is the acceptance gate for the whole redesign.

## Global constraints
- .NET 10; build 0 warnings; commit per slice; TDD; EphemeralMongo for real transactions.
- Engine enforces only irreducible invariants (balance, period freeze, optimistic concurrency); all authz/role/SoD policy lives in the host.
- Classification (account type, normal side, cash-flow activity) lives on the chart, never stored as a value; only journal-derived numbers are values.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
