# Audit Screens (Audit Trail + Verify Integrity) — Design

**Status:** Approved for planning
**Date:** 2026-07-16
**Branch:** `feat/audit-screens`

## Goal

Build the two per-client "Assurance ▸ Audit" screens the nav already promises but that currently fall through to the "Coming soon" placeholder:

1. **Audit Trail** (`/audit/trail`) — the client-wide, hash-chained audit log as a paginated table, with drill-in to the journal entry a row acted on.
2. **Verify Integrity** (`/audit/verify`) — an on-demand integrity check of the client's audit chain, reporting not just pass/fail but *where and how* the chain broke when it fails.

Both are backed by existing engine endpoints; this slice adds paging, a verify diagnostic, dedicated-capability enforcement, and the two frontend screens. Subledger Reconciliations and the deployment-admin control-plane Admin Audit are explicitly out of scope (separate later slices).

## Background & Current State

- **Backend endpoints (all under `/clients/{clientId}`, group `.RequireAuthorization()`):**
  - `GET /audit` → `GetClientAudit` — bare `List<AuditRecordResponse>`; accepts `skip`/`limit` (clamped 1–1000, default 200) but returns **no `Total`**. Gated `Permission.Read` (→ `gl.read`).
  - `GET /audit/verify` → `VerifyAudit` — `AuditVerifyResponse { bool Valid }` only. Gated `Permission.Read`.
  - `GET /audit/{entryId}` → `GetEntryAudit` — `List<AuditRecordResponse>` (one entry's timeline). Gated `Permission.Read`. Consumed by the journal **entry-detail** screen's inline audit sub-list.
- **Engine:** `MongoAuditLog` (`Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs`) — append-only, SHA-256 hash-chained, per-client 1-based `Sequence`, with a guarded `audit-head` document for tail-truncation detection. `VerifyAsync(clientId)` walks the chain (sequence contiguity, `PreviousHash` linkage, hash recompute) then reconciles against the head — but collapses every failure to a single `return false`.
- **Data shape** (`Backend/Accounting101.Ledger.Contracts/EntryResponses.cs`): `AuditRecordResponse(long Sequence, string Action, Guid? EntryId, int EntryVersion, DateTimeOffset At, string? Reason, ActorResponse Actor)`; `ActorResponse(Guid UserId, string? Name, IReadOnlyList<ClaimResponse> Claims)`. `Action` ∈ `Created, Approved, Voided, Superseded, Reversed, PeriodClosed, PeriodReopened, AccountCreated, AccountUpdated, DocumentCreated, DocumentUpdated, DocumentDeactivated, DocumentFinalized, DocumentSuperseded, DocumentVoided`. `EntryId` is null for account/document actions.
- **Enforcement model:** `LedgerGateway.ResolveAsync(user, clientId, Permission, ct)` authorizes by `membership.Capabilities.Contains(Capabilities.CapabilityForPermission(required))`. `Permission` maps 1:1 only to the nine `gl.*` caps. A dedicated `audit.read` capability (`Capabilities.AuditRead`) exists but is advisory — granted to all Reads role presets (`Auditor, Clerk, Approver, Controller, Admin`) and not actually enforced (the code comment notes enforcement "lands in a later slice" — this slice is that landing, for the two Audit-area screens).
- **Frontend:** `UI/Angular/src/app/core/audit/audit.service.ts` wraps only `GET /audit/{entryId}` (used by `features/journal/entry-detail.ts`). Nav (`layout/nav.ts`) already lists `Assurance ▸ Audit ▸ {Audit Trail /audit/trail, Verify Integrity /audit/verify, Subledger Reconciliations /audit/reconciliations}` with `area: 'audit'`, all currently resolving to the generic `Placeholder`. Shared building blocks to reuse: `shared/paginator.ts` (`<app-paginator [currentPage] [pageCount] (previous) (next)>`), `core/api/paged-response.ts` (`PagedResponse<T>{ items, total, skip, limit }`), `core/format/display.ts` (`money`, `displayDate`), `core/capabilities/can.directive.ts` (`*appCan`). Sibling exemplars for a paginated list screen: `features/journal/entry-list.ts` and `features/payroll/run-list.ts`.

## Design

### Backend

**B1. Audit-log pagination — dual-shape (mirrors `GET /entries`).**
`GET /clients/{id}/audit` becomes dual-shape:
- When **either** `skip` or `limit` is supplied → return `PagedResponse<AuditRecordResponse>(Items, Total, Skip, Limit)`.
- Otherwise → unchanged bare `List<AuditRecordResponse>` (preserves the entry-detail and any other bare consumer).

`Total` is the count of the client's audit records. Add `MongoAuditLog.CountForClientAsync(clientId, ct)` (a `CountDocuments(a => a.ClientId == clientId)`). The existing `skip`/`limit` clamp helpers (`Page()`/`PageLimit()`) are unchanged.

**B2. Verify diagnostic.**
`MongoAuditLog` gains `VerifyDetailedAsync(clientId, ct)` returning a diagnostic result; `VerifyAsync` is reduced to `(await VerifyDetailedAsync(...)).Valid` (behavior preserved for all existing callers). The result decomposes the failure `VerifyAsync` already detects:

```
AuditChainVerification(
    bool Valid,
    long RecordCount,          // records walked
    long? HeadSequence,        // the guarded audit-head sequence (null if no head)
    AuditChainFailure? Failure,// null when Valid
    long? BrokenAtSequence)    // the sequence at which the break was detected (see per-kind)
```

`AuditChainFailure` taxonomy (each mapped from the exact check in the current `VerifyAsync` walk):
- `SequenceGap` — `record.Sequence != expectedSeq` (a record missing / non-contiguous; also the first record not being sequence 1). `BrokenAtSequence = expectedSeq`.
- `BrokenLink` — `record.PreviousHash != previousHash` (chain linkage broken). `BrokenAtSequence = record.Sequence`.
- `HashMismatch` — `record.Hash != ComputeHash(record)` (record content tampered). `BrokenAtSequence = record.Sequence`.
- `TailTruncated` — the walk is internally clean but the guarded head's sequence exceeds the last walked record (newest N records deleted), or records are empty while a head with `Sequence > 0` exists. `BrokenAtSequence = lastWalkedSequence + 1` (the first missing record), or the head sequence when records are empty.
- `HeadMismatch` — the walk is clean and head sequence matches the last record, but the head hash ≠ the last record's hash, or the head is missing though records exist. `BrokenAtSequence = null`.

Order of checks matches today's walk: per-record failures (`SequenceGap`/`BrokenLink`/`HashMismatch`, first one wins) are detected during the walk; tail/head failures only after a clean walk.

The endpoint response `AuditVerifyResponse` is extended to carry the diagnostic (serializes `Failure` as its enum name string, camelCase host policy):
```
AuditVerifyResponse(bool Valid, long RecordCount, long? HeadSequence, string? Failure, long? BrokenAtSequence)
```
`VerifyAudit` maps `AuditChainVerification` → this response (`Failure?.ToString()`).

**B3. `audit.read` enforcement.**
Add to `LedgerGateway`:
```
Task<LedgerContext> ResolveCapabilityAsync(ClaimsPrincipal user, Guid clientId, string capability, CancellationToken ct)
```
Identical to `ResolveAsync` except it checks `membership.Capabilities.Contains(capability)` directly — no `Permission` enum, so the `PermissionToCapability`/`PermissionForCapability` bidirectional maps are untouched (avoids changing `PermissionForCapability`'s "null for non-gl.*" contract).

Switch **`GetClientAudit`** and **`VerifyAudit`** to `gateway.ResolveCapabilityAsync(user, clientId, Capabilities.AuditRead, ct)`.

**Deliberate boundary — `GET /audit/{entryId}` stays on `gl.read`.** It serves the inline audit sub-list on the `gl.read` journal entry-detail screen; flipping it to `audit.read` would couple journal viewing to the audit cap and risk hiding that sub-list for a bespoke `gl.read`-only grant. So `audit.read` gates the dedicated Audit **area** (the two new screens); the entry-timeline remains a journal-area concern.

**Migration note:** `audit.read` is in every Reads role preset, so aligning enforcement locks out no preset-holder. JordanSoft's owner holds the `Admin` preset (which includes `audit.read`) — verify at promote-smoke that the owner sees the Audit screens.

### Frontend

**F1. `audit.service.ts` + `audit.ts` additions.**
- Interfaces: `PagedResponse<T>` is reused from `core/api/paged-response.ts`; add `AuditVerifyResponse { valid: boolean; recordCount: number; headSequence: number | null; failure: string | null; brokenAtSequence: number | null; }`. `AuditRecordResponse` already exists.
- Methods: `clientAudit(skip: number, limit: number): Observable<PagedResponse<AuditRecordResponse>>` (GETs `/clients/{id}/audit?skip=&limit=`); `verify(): Observable<AuditVerifyResponse>` (GETs `/clients/{id}/audit/verify`).

**F2. Audit Trail screen** (`features/audit/audit-trail.ts`, `/audit/trail`).
Mirrors `entry-list.ts`/`payroll run-list.ts`: `skip`/`limit` signals, a `computed` query, `toSignal(toObservable(query).pipe(switchMap(svc.clientAudit(...))))`, `<app-paginator [currentPage] [pageCount] (previous) (next)>` where `pageCount = ceil(total/limit)`. Columns: **Date** (`displayDate(at)`), **Action**, **Actor** (`actor.name ?? actor.userId`), **Reason** (`reason ?? '—'`), **Entry** (the entry link). Default `limit` 50, `skip` 0.
- Rows whose `entryId` is non-null are whole-row clickable (`role="button" tabindex="0" cursor-pointer hover:bg-muted/50`, `(click)`/`(keydown.enter)`) → `router.navigate(['/journal', entryId])`, **gated on `gl.read` via the affordance** (a cross-area audit→GL drill, matching the established "View journal entry" `gl.read` gate). When the user lacks `gl.read`, or `entryId` is null (account/document actions), the row is not clickable.
- OnPush, zoneless, standalone.

**F3. Verify Integrity screen** (`features/audit/verify-integrity.ts`, `/audit/verify`).
A single action card. A "Check integrity" button calls `svc.verify()`. States: idle (prompt) / checking (spinner) / result. On `valid` → green: "Audit chain intact — {recordCount} records verified." On failure → red, humanizing the `failure` kind with `brokenAtSequence`:
- `HashMismatch` → "Tampered record at sequence {n}."
- `BrokenLink` → "Broken chain link at sequence {n}."
- `SequenceGap` → "Missing record at sequence {n}."
- `TailTruncated` → "Records deleted from the end of the chain (missing from sequence {n})."
- `HeadMismatch` → "Chain head mismatch — the recorded head does not match the chain tail."
Plus a generic fallback for an unknown `failure` string. Includes guidance to contact a deployment administrator on failure.

**F4. Routes + nav.**
- Add real routes: `/audit/trail` → `AuditTrail`, `/audit/verify` → `VerifyIntegrity`, and `/audit` redirects to `/audit/trail`. Remove these three from whatever placeholder fallback currently catches them (the `/audit/reconciliations` leaf stays on the placeholder — out of scope).
- Gating follows the established area-screen pattern — **no new route guard**: access = nav-gate (the nav leaves already carry `area: 'audit'`, so the group is shown only to `audit.read` holders) + backend `403` on the data calls (`GetClientAudit`/`VerifyAudit` now enforce `audit.read` per B3). Typing the URL directly loads the shell, whose data call then 403s — the same "403 unreachable via normal UI" posture used across the app. The plan confirms the exact nav→cap mapping for `area: 'audit'`.

### Wire shapes (backend record ↔ FE interface, host camelCase)
- `PagedResponse<AuditRecordResponse> { items: AuditRecordResponse[]; total; skip; limit }`
- `AuditRecordResponse { sequence; action; entryId: string|null; entryVersion; at; reason: string|null; actor: { userId; name: string|null; claims } }` (already exists FE-side)
- `AuditVerifyResponse { valid; recordCount; headSequence: number|null; failure: string|null; brokenAtSequence: number|null }`

## Testing

**Backend (xUnit, engine + host):**
- Pagination: `GET /audit?skip&limit` returns a `PagedResponse` with correct `Total`/`Items`/`Skip`/`Limit`; `GET /audit` (unpaged) still returns a bare array. `CountForClientAsync` returns the per-client count.
- Verify diagnostic: one test per failure kind, each forcing the condition against a seeded chain — `HashMismatch` (mutate a record's stored field so recompute differs), `BrokenLink` (rewrite a `PreviousHash`), `SequenceGap` (delete a middle record), `TailTruncated` (delete the newest record, head remembers), `HeadMismatch` (rewrite the head hash) — asserting `Valid=false`, the right `Failure`, and `BrokenAtSequence`. Plus the happy path: a clean chain → `Valid=true`, `Failure=null`, `RecordCount`/`HeadSequence` correct. `VerifyAsync` (bool) still returns the same pass/fail for every case.
- Gating: a member holding `audit.read` gets 200 on `/audit` and `/audit/verify`; a member without it gets 403; `/audit/{entryId}` remains reachable with `gl.read` (unchanged).

**Frontend (Vitest + TestBed):**
- Audit Trail: flushes a `PagedResponse` and asserts rows render (date/action/actor/reason), the paginator shows the right page count, Next/Prev re-query with updated `skip`; a row with an `entryId` (user has `gl.read`) navigates to `['/journal', entryId]`; a row without `entryId`, or without `gl.read`, does not navigate.
- Verify Integrity: valid response → green "records verified"; a `HashMismatch` response → red "Tampered record at sequence N"; a `TailTruncated` response → the truncation message. Uses the Vitest idioms (`vi.spyOn`, nav spy `.mockResolvedValue(true)`).

## Task Decomposition (5 tasks)

1. **Backend — audit-log pagination.** `MongoAuditLog.CountForClientAsync` + `GetClientAudit` dual-shape (`PagedResponse` when paged) + tests.
2. **Backend — verify diagnostic.** `AuditChainVerification` + `AuditChainFailure` + `MongoAuditLog.VerifyDetailedAsync` (+ `VerifyAsync` delegates) + extended `AuditVerifyResponse` + `VerifyAudit` mapping + per-failure-kind tests.
3. **Backend — `audit.read` gating.** `LedgerGateway.ResolveCapabilityAsync` + switch `GetClientAudit`/`VerifyAudit` to `Capabilities.AuditRead` + 403/allow tests.
4. **Frontend — Audit Trail.** `audit.service` `clientAudit` + `AuditVerifyResponse`/paged interface + `audit-trail` screen + `/audit/trail` route + nav wiring + spec.
5. **Frontend — Verify Integrity.** `audit.service` `verify` + `verify-integrity` screen + `/audit/verify` route + `/audit` redirect + spec.

## Global Constraints

- **Backend:** namespaces follow folder structure. Dual-shape `/audit` is additive (bare list preserved). `VerifyAsync`'s external `bool` behavior is unchanged. Display/enum names stable. Rider auto-converts explicit types to `var` — stage explicit file lists and check for stray churn before each commit.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. Audit-area screens gated on `audit.read`; the cross-area journal drill on a trail row is `gl.read`-gated via `*appCan`. FE test runner is **Vitest** (`vi.spyOn` global; nav spies `.mockResolvedValue(true)`).
- **Wire shapes** identical backend ↔ frontend (host `JsonNamingPolicy.CamelCase`). `Failure` serializes as the enum name string.
- Only touch files named per task. Do NOT change `GET /audit/{entryId}` gating, the entry-detail screen, subledger-reconciliation or admin-audit surfaces, or unrelated modules.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Out of Scope / Non-Goals

- Subledger Reconciliations screen (`/audit/reconciliations`) and the deployment-admin Admin Audit (`/admin/audit`) — separate later slices.
- Changing `GET /audit/{entryId}` enforcement or the journal entry-detail inline audit sub-list.
- Date/actor/action server-side filters on the audit log (only paging is added here).
- Repairing a broken chain — verify only diagnoses; remediation is a forensic/admin concern.
