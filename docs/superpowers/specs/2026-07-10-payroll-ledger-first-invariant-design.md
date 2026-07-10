# Payroll ledger-truth status — mirror of Cash (no engine change)

**Date:** 2026-07-10
**Status:** Design approved (brainstorming) — pending written-spec review → implementation plan
**Parent:** `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md` (§9 step 3 — generalize per module). Template: `docs/superpowers/specs/2026-07-09-cash-ledger-first-invariant-design.md` (the Cash core, shipped to master `6367aaa`). Part B (the engine entry guard) already shipped (`bc77ba1`). The engine batch read + `sourceRefs` param this design consumes already shipped with the Cash cycle.

---

## 1. Goal

Bring the Payroll module (`Modules/Payroll`) onto the subledger invariant. Payroll is a **structural twin of Cash**: examining the module shows it **already conforms** to ledger-first — it keeps no materialized balance, no `Allocation[]`, no per-entity subledger. So, exactly as with Cash, the work is two-part:

1. **Verify + document** that Payroll conforms, with a Payroll-scoped proof that the guard protects Payroll.
2. **Close the one genuine (narrow) gap:** a Payroll document's reported `Status` (Posted/Void) is derived from the engine **document envelope**, not the **ledger entry's** state. A crash between the two awaits of a void (reverse/withdraw the GL entry, then mark the doc void) can leave a doc reading `Posted` while its GL entry is reversed. Make the reported status derive from ledger-truth.

Payroll has **two** evidentiary doc types — `PayrollRun` and `TaxRemittance` — so every part is applied to both (Cash had two as well: deposit + disbursement).

**Cheaper than Cash:** the engine-side pieces the Cash cycle built — batch `MongoJournalStore.GetBySourceRefsAsync` + the `sourceRefs` CSV param on `GET /entries` — are already on master. **This design needs zero engine change.** It is a pure module-side mirror of the Cash status-truth work.

Deferred exactly as Cash deferred: any Angular/UI work. No new balance surface (Payroll correctly has none).

## 2. Why Payroll needs no AR/AP-style conversion (the verify finding)

Grounding read of the whole module (`PayrollRun`, `PayrollRunBody`, `TaxRemittance`, `TaxRemittanceBody`, `PayrollPosting`, `PayrollService`, `DocumentPayrollRunStore`, `PayrollEndpoints`, `PayrollPorts`, `ILedgerClient`):

- **No materialized financial balance anywhere.** No aggregate/balance/subledger endpoint in the module. Payroll liabilities (Withholdings Payable, Payroll Taxes Payable) are plain **aggregate GL accounts**, not per-run subledgers.
- **No `Allocation[]` equivalent, and remittances are not "applied" to runs.** `TaxRemittanceBody` states it: *"the module performs no outstanding-balance tracking."* `ComposeTaxRemittance` posts a standalone `Dr Withholdings-Payable / Dr Taxes-Payable / Cr Cash` with **no per-run dimension**. A run's `ComposePayrollRun` posts one balanced five-line entry with no dimensions either. Nothing to fold per-entity, nothing to dimension.
- **The stored amounts are a frozen post-time snapshot** of the clerk's pre-computed inputs (`Gross`, `EmployeeFica`, `EmployerFica`, `Deductions`, `IncomeTaxWithheld` for runs; `WithholdingsAmount`, `TaxesAmount` for remittances). The module performs no withholding-table math (module scope: journal translation only).
- **The store *is* the engine's document store** (`IDocumentStore`); `Number`/`Status` are *derived* from the envelope (`result.Sequence` → `PR-#####`; `result.State` → Posted/Void). The module owns no independent copy of GL state.
- **The one drift vector that applied — raw GL void/reverse of a `payroll`-stamped entry — is already closed by the shipped guard** (a single engine chokepoint covering every module). §5 adds a Payroll-scoped proof.

Conclusion: the redesign's premise ("a module keeps a materialized financial balance that duplicates GL state and can drift") does not apply to Payroll. No `Allocation[]` to delete, no fold to introduce, no `RequiredDimensions` to set, **no engine change**. The spec records this as the reason Payroll is a two-item hardening rather than a mirror.

## 3. Ledger-truth status (the one real change)

### 3.1 The resolver

A new **per-module** pure helper, a verbatim duplicate of Cash's `CashLedgerStatus` (a shared home is a deferred audit — see the design memory note; the codebase's brick-isolation convention favors per-module duplication of this ~8-line function for now):

```
PayrollLedgerStatus.ShowsVoided(entriesForOneDoc) -> bool
```

**Ledger says Void** iff either:
- the **primary** source entry (`ReversalOf == null`) has `Status == "Voided"` (withdrawn while pending), **or**
- **some** entry has `ReversalOf == primaryId` (reversed after posting).

**Reported `Status` = Void if (envelope-voided) OR (ledger-says-Void); else Posted** — the same safe *union* used by Cash. Ledger-truth can only **promote** to Void, never demote; it structurally closes the crash-between-awaits gap. **Fallback:** a doc with **no** source entries reports its envelope status (never throws on empty).

`includeVoided` list **filtering** stays keyed on the document envelope (unchanged) — the union rule governs only the *reported* status field.

The resolver is the single home of this logic in the Payroll module — shared by `PayrollRun` and `TaxRemittance`, and by both the single-read and list-read paths.

### 3.2 Read-path plumbing (no engine change)

The engine already exposes `GET /entries?sourceRefs=<csv>` (bare-array batch read) and `MongoJournalStore.GetBySourceRefsAsync` from the Cash cycle. Only module-side wiring is needed:

**Module (`Modules/Payroll`):**
- `ILedgerClient.GetEntriesBySourceRefsAsync(clientId, IReadOnlyList<Guid> sourceRefs, ct)` added; `HttpLedgerClient` implements it (calls `entries?sourceRefs=<csv>`; empty input → empty, no HTTP; forwards the caller's bearer, no module credential — it's a read). The test fake implements it.
- `PayrollService`:
  - `GetRunAsync` / `GetRemittanceAsync` — fetch the doc, fetch its entries via the existing singular `GetEntriesBySourceRefAsync`, overlay `Status` via the resolver.
  - New `ListRunsAsync` / `ListRemittancesAsync` (returning `PagedResponse<…>`) — fetch the page from the store, make **one** batch `GetEntriesBySourceRefsAsync` call for all page ids, group entries by `SourceRef`, overlay `Status` per row. One extra ledger round-trip **per page**, not per row.
- `PayrollEndpoints` — the two **list** handlers (`ListRuns`, `ListRemittances`) route through the new service methods instead of calling `IPayrollRunStore`/`ITaxRemittanceStore` directly; the detail handlers already go through the service.

## 4. Data model changes

None. No stored field added or removed. `PayrollRun`/`TaxRemittance`/their bodies are unchanged; the recipes (`PayrollPosting`), accounts (`PayrollPostingAccounts`), and the void/record lifecycle in `PayrollService` are unchanged. The only behavior change is that reported `Status` on reads now reflects ledger-truth.

## 5. Tests / proof

Front-loads the two findings the Cash final review surfaced (reversal branch only unit-tested; one doc type lacked a symmetric list test) — both branches and both doc types are covered from the start.

- **Resolver unit tests** (`PayrollLedgerStatus.ShowsVoided`): pending-withdrawn primary (`Voided`) → Void; posted + reversal entry present → Void; single `Active` posted entry → Posted; no entries → false (envelope fallback).
- **Service detail-overlay proofs** — for **both** `PayrollRun` and `TaxRemittance`, and **both** ledger-Void branches:
  - *withdrawn-while-pending*: withdraw the entry directly via the fake (`VoidAsync`), envelope stays Posted → `Get…Async` reports Void.
  - *reversed-after-posting*: create a reversal directly via the fake (`ReverseAsync`), envelope stays Posted → `Get…Async` reports Void.
- **Service list-overlay proofs** — for **both** doc types: two docs, one withdrawn at the ledger, `List…Async(includeVoided: true)` reports the withdrawn one Void and the other Posted.
- **E2E (batch ledger-truth on the list):** record two payroll runs, module-void one, list with `includeVoided=true` → the voided one reports Void, the other Posted (through the real host).
- **Payroll-scoped guard proof:** record + approve a payroll run, then attempt a **raw** GL reverse of its entry **without** the module credential → **409** ("correct through that module"). Confirms the guard covers Payroll.
- **Whole-solution reconciliation:** full suite green at the final commit.

## 6. Scope boundaries / non-goals

- No change to Payroll recipes, posting accounts, or the record/void lifecycle.
- **No engine change** (batch read + `sourceRefs` param already on master).
- No new Payroll balance/aggregate/subledger endpoint (Payroll rightly has none).
- No UI/Angular (deferred per parent spec).
- No shared-resolver extraction — deferred audit, recorded in the `accounting101-module-shared-sdk-audit` memory note.
- No backfill / migration (greenfield; no stored shape changes anyway).

## 7. Risks

- **Lower risk than Cash** — no shared-engine-surface change this cycle. The `sourceRefs` endpoint and batch store read are already merged and covered by their own engine tests.
- **Union rule subtlety:** reported status can now say Void while `includeVoided=false` still lists the doc (envelope not yet void). Intentional and safe (surfaces the void); documented so it isn't mistaken for a bug.
- **Two doc types double the surface** — every overlay/list/test exists twice (runs + remittances). The plan keeps them symmetric to avoid the coverage-asymmetry the Cash review flagged.

## 8. Sequencing (green at every commit)

1. `PayrollLedgerStatus.ShowsVoided` helper + unit tests. (Pure; no wiring yet.)
2. Module batch client method: `GetEntriesBySourceRefsAsync` on `ILedgerClient` + `HttpLedgerClient` + test fake. (Additive.)
3. `PayrollService` detail overlay (`GetRunAsync` + `GetRemittanceAsync`) + detail-overlay proofs (both branches, both doc types). (Detail reads now ledger-truth.)
4. `PayrollService` list methods (`ListRunsAsync` + `ListRemittancesAsync`) + `PayrollEndpoints` list re-route + list-overlay proofs (both doc types). (List now ledger-truth.)
5. E2E list-truth proof + Payroll-scoped guard proof + whole-solution reconciliation.

Order is the safety net: the resolver before it is wired; the client method before the list consumes it; detail before list; proofs alongside the behavior they lock.
