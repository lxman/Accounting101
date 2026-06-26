# Payroll Module (POC) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A new Payroll module: clerk records a payroll run + tax remittance; the module derives the balanced entry and posts it under its `payroll` credential. Translation only — POC. Mirrors the Payables module.

**Architecture:** `Modules/Payroll/Accounting101.Payroll` (domain: recipe, documents, service, accounts) + `Accounting101.Payroll.Api` (endpoints, HttpLedgerClient, `AddPayroll`) + `.Tests`. Third installed module → live N-module composition test. Use the **Payables** module as the file-by-file template.

**Tech Stack:** C#/.NET 10, MongoDB, the engine document store, xUnit + EphemeralMongo + WebApplicationFactory.

**Spec:** `docs/superpowers/specs/2026-06-26-payroll-module-design.md`

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Module never approves its own entries (post `PendingApproval`); posts under the `payroll` credential (slice 1) → entries stamped `viaModule="payroll"`.
- Configured account IDs (no hardcoded numbers); deductions lump into Withholdings Payable; no employee entity/dimension.
- Mirror Payables conventions (layout, naming, `Configured*AccountsProvider`, keyed `ModuleCredential`, manifest registration). New module projects mirror `Accounting101.Payables`/`.Api` csproj (Sdk, FrameworkReference, Usings).
- Run test classes one at a time (EphemeralMongo/host-boot flakiness). Stage explicit file lists; do NOT commit in a worktree.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## Task 1: PayrollPosting recipe + accounts (pure, the heart)

**Files (new project `Modules/Payroll/Accounting101.Payroll/`):**
- Create: `Accounting101.Payroll.csproj` (mirror `Accounting101.Payables.csproj`)
- Create: `PayrollPostingAccounts.cs` (5 account ids — mirror `BillPostingAccounts.cs`)
- Create: `PayrollRunBody.cs`, `TaxRemittanceBody.cs` (the input records)
- Create: `PayrollPosting.cs` (pure recipe — mirror `BillPosting.cs`)
- Test: new `Accounting101.Payroll.Tests/` project (mirror `Accounting101.Payables.Tests` csproj) — `PayrollPostingTests.cs`
- Modify: `Accounting101.slnx` (add the two new projects)

**Interfaces:**
- `PayrollRunBody(decimal Gross, decimal EmployeeFica, decimal EmployerFica, decimal Deductions, decimal IncomeTaxWithheld, DateOnly PayDate, string? Memo)`
- `TaxRemittanceBody(decimal WithholdingsAmount, decimal TaxesAmount, DateOnly PayDate, string? Memo)`
- `PayrollPostingAccounts { SalariesExpenseAccountId, PayrollTaxExpenseAccountId, CashAccountId, WithholdingsPayableAccountId, PayrollTaxesPayableAccountId }` (all `required Guid`)
- `PayrollPosting.ComposePayrollRun(Guid runId, PayrollRunBody body, PayrollPostingAccounts accounts) → PostEntryRequest` and `ComposeTaxRemittance(Guid id, TaxRemittanceBody body, PayrollPostingAccounts accounts) → PostEntryRequest`. Source types `"PayrollRun"` / `"TaxRemittance"`; entry `Id = EntryIdentity.ForSource(sourceType, docId)`; `SourceRef`/`SourceType` set; `EffectiveDate = PayDate`.

- [ ] **Step 1: Failing tests** (`PayrollPostingTests`, pure — no host):
  - `ComposePayrollRun` for (gross 28000, empFICA 2142, emprFICA 2142, deductions 0, incomeTax 5040) → 5 lines: Dr Salaries 28000, Dr PayrollTaxExp 2142, Cr Cash 20818, Cr Withholdings 5040, Cr PayrollTaxesPayable 4284; ΣDr=ΣCr.
  - deductions > 0 reduces Cash and adds to Withholdings (e.g. deductions 500 → Cash 20318, Withholdings 5540).
  - negative net pay (deductions+withholdings+empFICA > gross) → throws/`ArgumentException`.
  - `ComposeTaxRemittance` (withholdings 5040, taxes 4284) → Dr Withholdings 5040, Dr PayrollTaxesPayable 4284, Cr Cash 9324; balanced.
- [ ] **Step 2: Run, confirm fail** (project/types don't exist).
- [ ] **Step 3: Implement** the records + the pure `PayrollPosting` recipe exactly per the spec math. Mirror `BillPosting`'s structure (group/order lines for determinism; `EntryIdentity.ForSource`).
- [ ] **Step 4: Run, confirm pass.**
- [ ] **Step 5: Build clean, commit** (`feat(payroll): payroll run + tax remittance posting recipe`).

---

## Task 2: Documents, stores, and `PayrollService`

**Files:**
- Create: `PayrollRun.cs`/`PayrollRunStatus.cs`/`PayrollRunView.cs`, `TaxRemittance.cs`/`...Status`/`...View` (mirror `Bill`/`BillStatus`/`BillView`)
- Create: `DocumentPayrollRunStore.cs`, `DocumentTaxRemittanceStore.cs` (mirror `DocumentBillStore` — engine document store, evidentiary)
- Create: `PayrollPorts.cs` (store interfaces), `ILedgerClient.cs` (mirror Payables' port)
- Create: `PayrollService.cs` (record-and-post + void; mirror `BillService`/`BillPaymentService`)
- Test: `Accounting101.Payroll.Tests/PayrollServiceTests.cs` (vs an in-memory fake `ILedgerClient` + fake stores, like `Accounting101.Payables.Tests`)

**Interfaces:**
- `PayrollService.RecordRunAsync(clientId, PayrollRunBody, ct) → PayrollRun` (persists the doc, composes via Task 1, posts `PendingApproval` via `ILedgerClient`, links `SourceRef`). `VoidRunAsync(clientId, id, reason, ct)`. Same pair for tax remittance. Mirror how `BillService` finalizes+posts and how void reverses.

- [ ] **Step 1: Failing tests** — record a run → the fake ledger client received the balanced 5-line entry; the document is persisted and readable; status reflects posted. Void → the entry is reversed/voided through the client. Same for remittance.
- [ ] **Step 2: Run, confirm fail.**
- [ ] **Step 3: Implement** the documents, the two document stores (evidentiary, via `IDocumentStore`, mirror `DocumentBillStore`), and `PayrollService`. One-step record-and-post (no draft). Reuse the Task-1 recipe.
- [ ] **Step 4: Run, confirm pass** (Payroll.Tests classes individually).
- [ ] **Step 5: Build clean, commit** (`feat(payroll): documents, stores, and PayrollService (record/post/void)`).

---

## Task 3: API, host wiring, cross-host integration (N-module proof)

**Files:**
- Create: `Modules/Payroll/Accounting101.Payroll.Api/Accounting101.Payroll.Api.csproj` (mirror `Accounting101.Payables.Api`)
- Create: `HttpLedgerClient.cs` (mirror Payables' — forwards the user token AND sends `X-Module-Key: payroll` + `X-Module-Secret` from `[FromKeyedServices("payroll")] ModuleCredential` on `PostAsync`)
- Create: `PayrollServiceExtensions.cs` (`AddPayroll`: manifest `Plain`/`Evidentiary` collections `payroll-runs`,`tax-remittances`; `ModuleIdentity("payroll")` via `AddModule`; the `Configured*AccountsProvider`; the typed `HttpLedgerClient`), `PayrollEndpoints.cs` (`MapPayrollEndpoints` — the endpoints in the spec)
- Modify: `Accounting101.Host/Program.cs` (`builder.Services.AddPayroll(...)` + `app.MapPayrollEndpoints()`)
- Modify: `Accounting101.slnx` (add the Api + ensure Tests reference)
- Test: `Accounting101.Payroll.Tests/PayrollE2eTests.cs` (real host, all three modules)

**Interfaces:** consumes Task 2's `PayrollService`; slice-1's keyed `ModuleCredential` + module-posting path.

- [ ] **Step 1: Failing tests** (`PayrollE2eTests`, real host with Receivables + Payables + Payroll installed):
  - `POST /clients/{id}/payroll-runs` with the five numbers → 201; read the engine entry back → balanced, hits the five configured accounts with the right amounts, **`viaModule="payroll"`**, status `PendingApproval`.
  - the run document is retrievable; void works.
  - **N-module composition:** in the same host, a Payables bill and a Receivables invoice still post correctly (their document-store manifests aren't clobbered by Payroll). (Assert at least one cross-module operation succeeds alongside payroll.)
- [ ] **Step 2: Run, confirm fail** (endpoints/registration absent).
- [ ] **Step 3: Implement** the Api project, `AddPayroll` (manifest + identity + credential + accounts + client), the endpoints, and host wiring. Wire the configured accounts the same way Payables does (config section, e.g. `Payroll:Accounts:*`). Confirm the manifest registration composes alongside the existing two modules (if a one-module-per-host clobber surfaces — the umbrella's flagged risk — STOP and report it; that's a real platform finding, not something to paper over).
- [ ] **Step 4: Run, confirm pass** — `PayrollE2eTests` green; re-run a Payables + a Receivables E2E class to confirm the existing modules still work with Payroll installed. Full solution builds 0 warnings.
- [ ] **Step 5: Build clean, commit** (`feat(payroll): API, host wiring, and cross-host integration`).

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: `PayrollPostingTests`, `PayrollServiceTests`, `PayrollE2eTests`, plus one Payables + one Receivables E2E class (coexistence) — all green.
- [ ] Confirm: payroll run posts the exact balanced entry under `viaModule="payroll"`; remittance pays down the liabilities; void works; all three modules coexist in one host.
- [ ] Whole-branch review on the most capable model, then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: recipe (T1), documents/service/void (T2), API/host/credential-posting + N-module proof (T3).
- Type consistency: `PayrollPostingAccounts` field names match across recipe + provider; `ComposePayrollRun`/`ComposeTaxRemittance` signatures stable T1→T2→T3; `viaModule="payroll"` asserted in T3.
- Open implementer checks: (a) whether the host genuinely composes a third module without manifest clobber (T3 Step 3 — report if not); (b) the exact Payables csproj/registration patterns to mirror (read them first).
