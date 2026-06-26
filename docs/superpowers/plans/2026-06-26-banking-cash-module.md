# Banking/Cash Module (POC) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A new Cash module under `Modules/Banking/`: clerk records a cash disbursement (`Dr lines / Cr Cash`) or deposit (`Dr Cash / Cr lines`); the module derives the balanced entry and posts it `PendingApproval` under its `cash` credential. Translation only — POC. Scaffold a sibling `Reconciliation` project (empty) for later. Mirrors Payroll/Payables.

**Architecture:** `Modules/Banking/Cash/Accounting101.Banking.Cash` (domain) + `.Api` + `.Tests`. `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation` = empty scaffold. Fifth installed module → further N-module composition coverage. Use the **Payroll** module (just built) as the file-by-file template.

**Tech Stack:** C#/.NET 10, MongoDB, the engine document store, xUnit + EphemeralMongo + WebApplicationFactory.

**Spec:** `docs/superpowers/specs/2026-06-26-banking-cash-module-design.md`

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Module never approves its own entries (`PendingApproval`); posts under the `cash` credential (slice 1) → entries stamped `viaModule="cash"`.
- One configured cash account id (no hardcoded numbers); lines are `(accountId, amount)`, no dimension.
- Namespaces follow folder: `Accounting101.Banking.Cash`; module key `"cash"`, name `"Cash"`.
- Mirror Payroll/Payables conventions (layout, naming, `Configured*AccountsProvider`, keyed `ModuleCredential`, manifest, csproj). New csprojs mirror `Accounting101.Payroll`/`.Api`.
- Run test classes one at a time (EphemeralMongo/host-boot flakiness). Stage explicit file lists; do NOT commit in a worktree.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## Task 1: Banking layout + Cash recipe + Reconciliation scaffold

**Files (new):**
- `Modules/Banking/Cash/Accounting101.Banking.Cash/Accounting101.Banking.Cash.csproj` (mirror `Accounting101.Payroll.csproj`)
- `CashPostingAccounts.cs` (one `required Guid CashAccountId`)
- `CashDisbursementBody.cs`, `CashDepositBody.cs` (each: `IReadOnlyList<CashLine> Lines`, `DateOnly Date`, `string? Reference`, `string? Memo`; `CashLine(Guid AccountId, decimal Amount)`)
- `CashPosting.cs` (pure recipe — mirror `PayrollPosting`/`BillPosting`)
- `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/` (mirror Payroll.Tests csproj) — `CashPostingTests.cs`
- `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/Accounting101.Banking.Reconciliation.csproj` (empty class library, `net10.0`) + one placeholder file `ReconciliationModule.cs` (a doc-comment marker class: "Reconciliation module — implemented in a later slice; intentionally empty.")
- Modify: `Accounting101.slnx` (add the three new projects — Cash, Cash.Tests, Reconciliation)

**Interfaces:**
- `CashLine(Guid AccountId, decimal Amount)`
- `CashPosting.ComposeDisbursement(Guid id, CashDisbursementBody body, CashPostingAccounts accounts) → PostEntryRequest`: `Dr` each line (group by account, order by id; sum), `Cr CashAccountId` = total. SourceType `"CashDisbursement"`; `Id = EntryIdentity.ForSource("CashDisbursement", id)`; `EffectiveDate = Date`; `SourceRef`/`SourceType` set.
- `CashPosting.ComposeDeposit(Guid id, CashDepositBody body, CashPostingAccounts accounts) → PostEntryRequest`: `Dr CashAccountId` = total, `Cr` each line. SourceType `"CashDeposit"`.
- Both reject: empty lines, any non-positive line amount, or a line whose account == `CashAccountId`.

- [ ] **Step 1: Failing tests** (`CashPostingTests`, pure):
  - disbursement (lines: Interest 500, LoanPayable 1500 → `Dr 500, Dr 1500, Cr Cash 2000`); ΣDr=ΣCr; lines sharing an account collapse; deterministic order.
  - deposit (lines: Members'Capital 25000 → `Dr Cash 25000, Cr 25000`); balanced.
  - empty lines → throws; a zero/negative amount → throws; a line account == CashAccountId → throws.
  - SourceType/`EntryIdentity.ForSource` id is stable + distinct between disbursement/deposit.
- [ ] **Step 2: Run, confirm fail.**
- [ ] **Step 3: Implement** the bodies, `CashPostingAccounts`, the pure `CashPosting` recipe (mirror `PayrollPosting`/`BillPosting` line grouping + `EntryIdentity.ForSource`), and the empty Reconciliation scaffold project. Add all to slnx.
- [ ] **Step 4: Run, confirm pass; full solution builds (incl. the empty Reconciliation project) 0 warnings.**
- [ ] **Step 5: Build clean, commit** (`feat(banking): Cash disbursement/deposit recipe + Reconciliation scaffold`).

---

## Task 2: Cash documents, stores, and `CashService`

**Files (in `Accounting101.Banking.Cash`):**
- `CashDisbursement.cs`/`CashDisbursementStatus.cs`/`CashDisbursementView.cs`, `CashDeposit.cs`/`...Status`/`...View` (mirror Payroll's `PayrollRun`/status/view)
- `DocumentCashDisbursementStore.cs`, `DocumentCashDepositStore.cs` (evidentiary, via `IDocumentStore` — mirror `DocumentPayrollRunStore`)
- `CashPorts.cs` (store interfaces), `ILedgerClient.cs` (mirror Payroll's port)
- `CashService.cs` (`RecordDisbursementAsync`/`VoidDisbursementAsync` + deposit pair; one-step record-and-post `PendingApproval`, void by `SourceRef` — mirror `PayrollService`)
- Test: `Accounting101.Banking.Cash.Tests/CashServiceTests.cs` (vs in-memory fakes — mirror `PayrollServiceTests`/Payables fakes)

- [ ] **Step 1: Failing tests** — record a disbursement → fake `ILedgerClient` got the balanced `Dr lines / Cr Cash`; document persisted + readable; status Posted. Void → entry reversed; doc Void. Same for deposit.
- [ ] **Step 2: Run, confirm fail.**
- [ ] **Step 3: Implement** documents, the two evidentiary stores, and `CashService` (reuse the Task-1 recipe; never approve; void by SourceRef). Mirror `PayrollService` exactly.
- [ ] **Step 4: Run, confirm pass** (Cash.Tests classes individually).
- [ ] **Step 5: Build clean, commit** (`feat(banking): Cash documents, stores, and CashService`).

---

## Task 3: Cash API, host wiring, cross-host integration

**Files:**
- `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/Accounting101.Banking.Cash.Api.csproj` (mirror `Accounting101.Payroll.Api`)
- `HttpLedgerClient.cs` (forwards user token; `PostAsync` sends `X-Module-Key: cash` + `X-Module-Secret` from `[FromKeyedServices("cash")] ModuleCredential`; approve/reverse/void forward the user token)
- `CashServiceExtensions.cs` (`AddCash`: manifest evidentiary `cash-disbursements`,`cash-deposits`; `AddModule(new ModuleIdentity("cash"), "Cash", …)`; `ConfiguredCashAccountsProvider` reading `Cash:Accounts:Cash`; `CashService`, stores, typed `HttpLedgerClient`), `CashEndpoints.cs` (`MapCashEndpoints` — the spec's endpoints + `RequireAuthorization`)
- Modify: `Accounting101.Host/Program.cs` (`AddCash` + `MapCashEndpoints`)
- Modify: `Accounting101.slnx` (add the Api project; ensure Tests references it)
- Test: `Accounting101.Banking.Cash.Tests/CashE2eTests.cs` (real host, all modules installed)

- [ ] **Step 1: Failing tests** (`CashE2eTests`, real host, Receivables+Payables+Payroll+Cash; seed the cash + line accounts + config `Cash:Accounts:Cash`):
  - `POST /clients/{id}/cash-disbursements` (lines Interest 500, LoanPayable 1500) → 201; engine entry → `Dr 500, Dr 1500, Cr Cash 2000`, balanced, `PendingApproval`, **`viaModule="cash"`**.
  - `POST /clients/{id}/cash-deposits` (Members'Capital 25000) → 201; `Dr Cash 25000 / Cr 25000`, `viaModule="cash"`.
  - the documents retrievable; void works.
  - **N-module coexistence:** a Payables bill (or Payroll run) still posts correctly in the same host (no manifest clobber with five modules).
- [ ] **Step 2: Run, confirm fail.**
- [ ] **Step 3: Implement** the Api, `AddCash`, the credentialed `HttpLedgerClient`, endpoints, host wiring, config-driven accounts (mirror Payroll). Confirm five-module composition; if a manifest clobber surfaces, STOP and report (platform finding).
- [ ] **Step 4: Run, confirm pass** — `CashE2eTests` green; re-run one Payables + one Payroll E2E class (coexistence). Full solution 0 warnings.
- [ ] **Step 5: Build clean, commit** (`feat(banking): Cash API, host wiring, and cross-host integration`).

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings (incl. the empty Reconciliation project).
- [ ] Run individually: `CashPostingTests`, `CashServiceTests`, `CashE2eTests`, plus one Payables + one Payroll E2E (coexistence) — all green.
- [ ] Confirm: disbursement/deposit post the balanced entries under `viaModule="cash"`; void works; five modules coexist; Reconciliation project exists but is empty/unregistered.
- [ ] Whole-branch review on the most capable model, then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: recipe both directions + rejects (T1), documents/service/void (T2), API/host/credential-posting + N-module proof (T3), Reconciliation scaffold (T1).
- Type consistency: `CashLine`/`CashPostingAccounts`/`Compose*` signatures stable T1→T3; `viaModule="cash"` asserted in T3.
- Open implementer checks: (a) five-module composition holds (T3 — report if not); (b) mirror Payroll's csproj/registration/store patterns (read them first); (c) the empty Reconciliation project must not trip the 0-warnings gate (an empty class lib is fine; add the placeholder file).
