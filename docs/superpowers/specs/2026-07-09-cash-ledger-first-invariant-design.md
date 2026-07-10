# Cash ledger-first invariant — verify + prove + ledger-truth status

**Date:** 2026-07-09
**Status:** Design approved (brainstorming) — pending written-spec review → implementation plan
**Parent:** `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md` (§9 step 3 — generalize per module). Siblings: `2026-07-09-ar-ledger-first-core-design.md` (shipped `3af54fc`), `2026-07-09-ap-ledger-first-core-design.md` (shipped `cbcbd00`). Part B (the engine entry guard) already shipped (`bc77ba1`).

---

## 1. Goal

Bring the Cash module (`Modules/Banking/Cash`) onto the subledger invariant. Unlike AR and AP, this is **not** a mechanical mirror: examining the module shows Cash **already conforms** to ledger-first — it keeps no materialized balance, no `Allocation[]`, no per-entity subledger. So the work is two-part:

1. **Verify + document** that Cash conforms, with a proof test that the guard protects Cash, so its conformance is *evidenced* rather than merely asserted.
2. **Close the one genuine (narrow) structural gap:** a Cash document's reported `Status` (Posted/Void) is derived from the engine's **document envelope**, not from the **ledger entry's** state. A crash between the two awaits of a void (reverse the GL entry, then mark the doc void) can leave a doc reading `Posted` while its GL entry is already reversed. Make the reported status derive from ledger-truth so that gap is structurally closed.

Deferred exactly as AR/AP deferred: any Angular/UI work. No new Cash balance surface (Cash correctly has none).

## 2. Why Cash needs no AR/AP-style conversion (the verify finding)

Grounding read of the whole module (`CashDeposit`, `CashDisbursement`, `CashLine`, `CashPosting`, `CashService`, `DocumentCashDepositStore`/`DocumentCashDisbursementStore`, `CashEndpoints`, `CashPorts`):

- **No materialized financial balance anywhere.** There is no "cash subledger balance" — no aggregate/balance/reconciliation endpoint in the module. The Cash-account balance simply *is* the GL Cash account balance. Contrast FA/Inventory, which store materialized balances (the hard case).
- **No `Allocation[]` equivalent.** A deposit/disbursement is a standalone cash movement, fully consumed at posting — it is never "applied" to anything, so there is nothing to fold per-entity and nothing to dimension. Contrast AR/AP, whose drift vector was the mutable `Allocation[]`.
- **The stored `Lines` (`AccountId` + `Amount`) are a frozen post-time snapshot** of the clerk's inputs, composed once into one balanced journal entry and never re-allocated. This is exactly the "frozen snapshot metadata" pattern AR *settled on* for line-items — permitted, not a duplicated balance.
- **The store *is* the engine's document store** (`IDocumentStore`), not a separate DB. `Number` and `Status` are *derived* from the engine document envelope (`result.Sequence` → `CR-#####`; `result.State` → Posted/Void). The module owns no independent copy of GL state.
- **The one drift vector that applied to Cash — raw GL void/reverse of a `cash`-stamped entry — is already closed by the shipped guard** (a single engine chokepoint covering every module). §5 adds a Cash-scoped proof of this.

Conclusion: the redesign's premise ("a module keeps a materialized financial balance that duplicates GL state and can drift") does not apply to Cash. No `Allocation[]` to delete, no fold to introduce, no `RequiredDimensions` to set, **no engine dimension work**. The spec records this as the reason Cash is a two-item hardening rather than a mirror.

## 3. Ledger-truth status (the one real change)

### 3.1 The resolver

A single pure helper decides a document's reported status from its own source entries:

```
CashLedgerStatus.Resolve(envelopeStatus, entriesForDoc, sourceRef) -> Posted | Void
```

**Ledger says Void** iff either:
- the **primary** source entry (`ReversalOf == null`) has `Status == "Voided"` (withdrawn while pending — the engine flips the single entry to `Voided`, no reversal spawned), **or**
- **some** entry has `ReversalOf == primaryId` (reversed after posting — a reversal entry exists; the original stays `Active`).

**Reported `Status` = Void if (envelope-voided) OR (ledger-says-Void); else Posted.** This is a safe *union* (user-approved): ledger-truth can only ever **promote** a doc to Void, structurally closing the crash-between-awaits gap (GL reversed, doc-void await failed → today wrongly reads Posted). It can never regress an envelope-voided doc back to Posted, and a transient failure of the entries read cannot flip a stored-void doc to Posted.

**Fallback:** if a doc has **no** source entries (an anomaly for a recorded doc — one is always posted at record), report the envelope status. The resolver never throws on empty input.

`includeVoided` list **filtering** stays keyed on the document envelope (unchanged) — the union rule governs only the *reported* status field, so a ledger-voided-but-envelope-Posted doc still appears in the default (non-void) list, correctly surfacing its Void status rather than hiding it.

The resolver is the single home of this logic — shared by deposit and disbursement, and by both the single-read and list-read paths. No duplication.

### 3.2 Read-path plumbing (detail + list, no N+1 — Approach B)

**Engine (`Backend/Accounting101.Ledger.*`):**
- `IJournalStore.GetBySourceRefsAsync(clientId, IReadOnlyList<Guid> sourceRefs, ct)` — batch of `GetBySourceRefAsync`, implemented in Mongo as `f.In(e => e.SourceRef, sourceRefs)` on the existing `(client, sourceRef)` index; the in-memory test store mirrors it. Empty input → empty result (no DB round-trip).
- `ListEntries` endpoint gains an optional **`sourceRefs` CSV** query param (`?sourceRefs=g1,g2`; user-approved shape, matching the singular `sourceRef` style). It slots into the query-precedence chain as a **peer of `sourceRef`** and returns the same **bare array** the singular branch does (an internal aggregation read). Malformed CSV (any element not a Guid) → **400**. Present-but-empty (`sourceRefs=`) → empty bare array. When both `sourceRef` and `sourceRefs` are supplied, `sourceRef` keeps precedence (documented; not a supported combination).

**Module (`Modules/Banking/Cash`):**
- `ILedgerClient.GetEntriesBySourceRefsAsync(clientId, IReadOnlyList<Guid> sourceRefs, ct)`; `HttpLedgerClient` calls `entries?sourceRefs=<csv>` (forwarding the caller's bearer, no module credential — it's a read). The test fake implements it.
- `CashService`:
  - `GetDepositAsync` / `GetDisbursementAsync` — fetch the doc from the store, fetch its entries via the existing singular `GetEntriesBySourceRefAsync`, overlay `Status` via the resolver.
  - New list methods (`ListDepositsAsync` / `ListDisbursementsAsync` returning `PagedResponse<…>`) — fetch the page from the store, make **one** batch `GetEntriesBySourceRefsAsync` call for all page ids, group entries by `SourceRef`, overlay `Status` per row via the resolver. One extra ledger round-trip **per page**, not per row.
- `CashEndpoints` — the **list** handlers route through the new service methods instead of calling the store directly; the detail handlers already go through the service.

## 4. Data model changes

None. No stored field added or removed. `CashDeposit`/`CashDisbursement`/`CashLine`/bodies are unchanged; the recipes (`CashPosting`), accounts (`CashPostingAccounts`), and the void/record lifecycle in `CashService` are unchanged. The only behavior change is that reported `Status` on reads now reflects ledger-truth.

## 5. Tests / proof

- **Resolver unit tests** (`CashLedgerStatus.Resolve`): pending-withdrawn primary (`Voided`) → Void; posted + reversal entry present → Void; single `Active` posted entry → Posted; envelope-void + any/no entries → Void; **no entries** → envelope fallback; `sourceRef` mismatch ignored.
- **Engine tests:** `GetBySourceRefsAsync` returns the union across several refs and excludes unrelated docs; `ListEntries?sourceRefs=` returns a bare array; malformed CSV → 400; empty → empty array; existing single-`sourceRef`/`reference`/`dimension`/`account`/paging branches unchanged (regression guard).
- **Service-level overlay proof** (the crash the E2E can't reproduce): with a fake ledger client returning a **reversed** entry set while the fake store's envelope says **Posted**, `GetDepositAsync` and `ListDepositsAsync` report **Void** — proving the overlay does real work beyond the envelope.
- **E2E (batch ledger-truth on the list):** record two deposits, module-void one, list with `includeVoided=true` → the voided one reports Void, the other Posted; the same for disbursements.
- **Cash-scoped guard proof (§2 verify half):** record + approve a deposit, then attempt a **raw** GL reverse/void of its entry **without** the module credential → **409** ("correct through that module"). Confirms the invariant's enforcement arm covers Cash.
- **Whole-solution reconciliation:** full suite green at the final commit.

## 6. Scope boundaries / non-goals

- No change to Cash recipes, posting accounts, or the record/void lifecycle.
- No new Cash balance/aggregate/subledger endpoint (Cash rightly has none).
- No UI/Angular (deferred per parent spec).
- The Banking **Reconciliation** module is untouched — a separate concern that already reads the ledger directly.
- No backfill / migration (greenfield; no stored shape changes anyway).

## 7. Risks

- **`ListEntries` is shared by every module's ledger client.** The new `sourceRefs` branch must not perturb the existing single-`sourceRef`, `reference`, `dimension`, `account`, posting-only, or unfiltered/paging branches. It enters as a peer of `sourceRef` with identical bare-array semantics; the regression test in §5 locks this.
- **CSV Guid parsing** at the endpoint must reject malformed input as 400 (not 500, not silent-empty). Explicit parse + validate.
- **Union rule subtlety:** reported status can now say Void while `includeVoided=false` still lists the doc (envelope not yet void). This is intentional and safe (surfaces the void); documented so it isn't mistaken for a bug.

## 8. Sequencing (green at every commit)

1. Engine batch read: `IJournalStore.GetBySourceRefsAsync` + Mongo + in-memory + `ListEntries` `sourceRefs` param + engine tests. (Additive; no existing behavior changes.)
2. Module ledger client: `GetEntriesBySourceRefsAsync` on `ILedgerClient` + `HttpLedgerClient` + test fake. (Additive.)
3. `CashLedgerStatus.Resolve` helper + unit tests. (Pure; no wiring yet.)
4. `CashService` detail overlay (`Get*Async`) + service-level overlay proof. (Detail reads now ledger-truth.)
5. `CashService` list methods + `CashEndpoints` list re-route + E2E batch proof. (List now ledger-truth.)
6. Cash-scoped guard proof + whole-solution reconciliation.

Order is the safety net: engine/plumbing before the module consumes it; the resolver before it is wired; detail before list; proofs alongside the behavior they lock.
