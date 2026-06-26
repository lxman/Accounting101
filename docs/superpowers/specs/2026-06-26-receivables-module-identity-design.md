# Slice 5 — Receivables module-identity posting — Design

**Date:** 2026-06-26
**Status:** Spec for review
**Umbrella:** [MVP Module Architecture](2026-06-26-mvp-module-architecture-design.md) — build-sequence slice 5 ("migrate Receivables + Payables to module-identity posting"). Payables/Payroll/Cash already post under their credential; **Receivables is the lone holdout** — this slice migrates it, completing the slice. After this, every entry a clerk legitimately creates flows through a module identity, which is the precondition for slice 6 (remove raw `Post` from Clerk).

## Goal

Receivables currently posts by **forwarding only the user's bearer token** (`ViaModule = null`), unlike the other modules. Migrate it so its invoices, payments, and the slice-4 dispositions all post under the `receivables` credential and are stamped `viaModule="receivables"`. This is mechanical credential wiring (mirroring Payables) **plus one engine change**, surfaced by the audit below.

## The engine wrinkle (load-bearing)

`InvoiceService.IssueAsync` runs a **pre-flight dry-run** via `ILedgerClient.ValidateAsync` (engine `POST /entries/validate`) before posting the A/R entry. That endpoint authorizes with `gateway.ResolveAsync(user, clientId, Permission.Post)` — it requires the **user** to hold `Post` and is **not** module-aware (unlike `PostEntry`, which uses `ResolveForPostAsync`). So once slice 6 removes `Post` from the Clerk, the issue-invoice pre-flight would break.

**Fix:** make `ValidateEntry` module-aware — use `ResolveForPostAsync(user, clientId, moduleAuth, ct)` exactly like `PostEntry`. This is **backward-compatible**: `ResolveForPostAsync` falls back to user-`Post` when no module credential is present, so existing user-`Post` callers are unaffected; a module credential now also authorizes the dry-run. Validate writes nothing, so the `ViaModule` it threads through `TryMapEntry` is harmless — the change is purely about **authorization**: a dry-run *of* a post should authorize *like* a post.

## Changes

### 1. Engine — `ValidateEntry` module-aware (`Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs`)
Add an `IModuleAuthenticator moduleAuth` parameter to the `ValidateEntry` handler and replace `gateway.ResolveAsync(user, clientId, Permission.Post, ct)` with `gateway.ResolveForPostAsync(user, clientId, moduleAuth, ct)` (mirror `PostEntry`). Nothing else changes — `ValidateForPostAsync` is untouched.

### 2. Receivables `HttpLedgerClient` (`Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`)
Inject `[FromKeyedServices("receivables")] ModuleCredential credential` (registered by `AddReceivables` → `AddModule(new ModuleIdentity("receivables"), …)`). Attach `X-Module-Key` / `X-Module-Secret` on **`PostAsync`** and **`ValidateAsync`** (both are new-entry origination / its dry-run, so both authorize under the module path). **`ApproveAsync`/`ReverseAsync`/`VoidAsync` are unchanged** — they authorize Approve/Reverse/Void permissions, not Post; the user carries those (mirrors Payables). **Preserve** the existing richer error handling (`EnsureSuccessAsync` / `ReasonFrom` → `LedgerClientException`) — do not replace it with bare `EnsureSuccessStatusCode`.

### 3. Proof — `ModuleViaReceivablesTests` (`Modules/Receivables/Accounting101.Receivables.Tests/`)
Mirror `ModuleViaPayablesTests`: seed a SoD client + chart, **issue an invoice** as the clerk, read the engine entry by `sourceRef`, assert `ViaModule == "receivables"`. Add a second assertion that a **payment** (a disposition path) also stamps `viaModule="receivables"`. The issue path exercises `ValidateAsync` + `PostAsync` together, so it also proves the credentialed validate path works end-to-end.

## Out of scope (named)
- **Removing `Post` from the Clerk role** — that's slice 6 (this slice only makes the module path complete so slice 6 is safe). A focused "validate authorizes without user `Post`" test naturally lands in slice 6 when the Clerk role actually loses `Post`; here, the green issue-invoice tests (clerk still has `Post`) prove no regression and the credential path is wired.
- Payables/Payroll/Cash — already migrated.
- viaModule on Approve/Reverse/Void — by design those are not module-originated.

## Testing
- `ModuleViaReceivablesTests`: invoice issue → `viaModule="receivables"`; payment → `viaModule="receivables"`.
- Engine: a `Ledger.Api.Tests` assertion that `POST /entries/validate` still succeeds for a user with `Post` (no regression from the `ResolveForPostAsync` swap). If the engine test suite already has a module-credential post test, add a parallel validate case asserting a module credential authorizes the dry-run; otherwise the Receivables issue E2E (which calls validate under the credential) is the integration proof.
- Regression: existing `ReceivablesIssueTests`, `CashApplicationTests`, `ReceivablesDispositionsE2eTests`, and the engine's existing post/validate tests stay green.

## Global constraints
- .NET 10; build 0 warnings; commit per task; TDD; EphemeralMongo (run test classes individually).
- Engine change must be **backward-compatible** (user-`Post` callers unaffected) — verified by the existing validate/post tests staying green.
- Mirror Payables' credential pattern; **keep** Receivables' `LedgerClientException` error handling.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
