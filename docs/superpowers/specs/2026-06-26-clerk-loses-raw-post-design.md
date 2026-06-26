# Slice 6 — Remove raw GL write from the Clerk role — Design

**Date:** 2026-06-26
**Status:** Spec for review
**Umbrella:** [MVP Module Architecture](2026-06-26-mvp-module-architecture-design.md) — build-sequence slice 6 ("remove raw `Post` from Clerk"), the payoff, gated on slices 1–5 (now all merged).

## Goal

This is the change the whole redesign was building toward. A Clerk currently holds `{Read, Post, Revise}` — `Post` and `Revise` are raw-GL write permissions that let a clerk write journal entries directly, bypassing the modules. The 24-month dog-food caught exactly this (an AP clerk posting vendor bills as plain journal entries). Now that every clerk cycle has a module path **and** every module posts under its own credential (slices 1–5), we close the raw-GL door for clerks:

**`Clerk = {Read}`** — Read for queries; **all writes go through a module** (which posts under the module credential, authorized by membership, not by the user's `Post`). Raw GL is the accountant's/controller's desk (Controller keeps `Post`/`Revise`; SoD: accountant makes GL JEs, controller/approver approves).

## Why both `Post` and `Revise` (not just `Post`)

`Post` is the obvious door (create a raw entry). `Revise` is the second door: it rewrites an existing entry into a new version — including a module-created entry — directly via raw GL. Leaving `Revise` would let a clerk edit any entry outside the module, defeating the purpose. Both are raw-GL writes a clerk no longer needs (modules post + void/reverse; they never revise). So the Clerk loses both, becoming `{Read}` only. (Approver/Controller/Admin unchanged.)

## Why this is safe now (gated on 1–5)

A clerk with `{Read}` can still do everything through modules because module posting does **not** consult the user's `Post`:
- Module write → engine `POST /entries` with `X-Module-Key`/`X-Module-Secret` → `ResolveForPostAsync` authorizes the **module** (registered + enabled + user-is-member), stamping `viaModule`. The user needs only membership.
- The issue-invoice **pre-flight** → `POST /entries/validate` is module-aware as of slice 5 (same `ResolveForPostAsync` path) — so it authorizes under the credential too. (This is why slice 5 had to precede slice 6.)
- Module **reads** (settlement views, `GetEntriesBySourceRef`) → forward the user token → need `Read`, which the clerk keeps.
- Module **void/reverse/approve** → forward the user token → need `Void`/`Reverse`/`Approve`, which the clerk never had (those are the Approver's — SoD intact).

Every clerk cycle has a module path: AR invoices/receipts/dispositions (Receivables), AP bills/payments (Payables), payroll + tax remittance (Payroll), loan/insurance/tax-payment disbursements (Cash). Nothing is stranded.

## The change

### 1. Matrix (`Backend/Accounting101.Ledger.Api/Control/Authorization.cs`)
```csharp
[LedgerRole.Clerk] = [Permission.Read],     // was: [Permission.Read, Permission.Post, Permission.Revise]
```
Nothing else in the matrix changes.

### 2. Engine policy tests (`Backend/Accounting101.Ledger.Api.Tests/PolicyTests.cs`)
Exactly two methods encoded the old Clerk matrix and must change:
- **`A_clerk_can_post_but_cannot_approve`** → rewrite as **`A_clerk_cannot_post_or_revise_raw_entries`**: a Clerk gets `403 Forbidden` on raw `POST /entries` **and** on raw `POST /entries/{id}/revise`. (The "cannot approve" coverage is retained by the existing approver/SoD tests; this method's job is now the raw-write denial.)
- **`An_approver_can_approve_but_cannot_post`** → its setup posts an entry *via a Clerk* (so the approver has something to approve). Since a Clerk can no longer post raw, switch that poster to a **Controller** (which keeps `Post`) — e.g. `await fixture.AddMemberAsync(c.ClientId, LedgerRole.Controller)`. The approver-approves / approver-cannot-post assertions are unchanged.

`A_clerk_cannot_reverse`, the auditor test, and the revise/self-approve tests (which use the default **Controller** member) are unaffected and stay green.

### 3. No production change beyond the matrix
The engine already denies raw `Post`/`Revise` to anyone lacking the permission (`ResolveForPostAsync` falls back to `ResolveAsync(Permission.Post)`; `ReviseEntry` uses `ResolveAsync(Permission.Revise)`). Removing the permissions from the Clerk is the entire mechanism — no endpoint code changes.

## The proof (regression = the positive case)

The "a clerk can still do its job through modules" proof is the **existing module E2E suites**, which seed a `Clerk` member and drive every write through the module credential. After the matrix change they must **stay green** — that is the live evidence the clerk path is intact with `{Read}` only:
- `ModuleViaReceivablesTests`, `CashApplicationTests`, `ReceivablesDispositionsE2eTests`, `ReceivablesIssueTests` (Receivables: issue, receipt, dispositions as a Clerk)
- `ModuleViaPayablesTests` (Payables: enter a bill as a Clerk)
- Payroll + Cash E2E (record-and-post as a Clerk)

If any of these fails, it means that path secretly depended on the user's `Post` — a real finding to surface, not paper over.

## Out of scope (named)
- **Sim briefs + dataset + re-run** — slice 7. (After this change, the sim's raw-JE clerk steps would 403; slice 7 switches them to module endpoints and re-runs the 2-year dog-food. Expected and sequenced.)
- Role redefinition beyond removing the two permissions; no new roles; no change to Approver/Controller/Admin.
- Bank Reconciliation module (separate, deferred).

## Testing
- `PolicyTests`: clerk `403` on raw post + raw revise; approver flow (Controller posts, approver approves) green; auditor/reverse/SoD tests green.
- Regression (the positive proof): all module E2E suites above stay green with the new matrix.
- `AccountTests`/`OnboardingTests` clerk cases (which assert a clerk lacks `ManageAccounts`) stay green — unaffected by removing `Post`/`Revise`.

## Global constraints
- .NET 10; build 0 warnings; TDD; EphemeralMongo (run test classes individually).
- Surgical: the only production change is the one matrix line. Test changes limited to the two `PolicyTests` methods that encoded the old Clerk matrix.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
