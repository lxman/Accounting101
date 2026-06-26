# Module-Poster Identity + Actor Stamping — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let a module post a journal entry authorized as the *module* (its own credential) while the engine stamps the originating *user* as the actor and records `viaModule` on the entry. Purely additive — raw user-token posting and existing behavior unchanged; the clerk keeps `Post` (removed later, slice 6).

**Architecture:** Two independently-validated identities on a module post — the user (from the validated bearer token = actor) and the module (from a per-module credential = authorization principal). Reuse the existing `IModuleAuthenticator` seam, `ModuleAccess` dual-auth shape, and `ModuleRegistration`/`ControlStore`. Grow `AuditStamp` by one nullable `ViaModule`.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, MongoDB, xUnit + EphemeralMongo + WebApplicationFactory.

**Spec:** `docs/superpowers/specs/2026-06-26-module-poster-identity-design.md`

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- **Additive only** — raw posting (`POST /entries` with a `Post`-holding user, no module headers) behaves exactly as today; `ViaModule = null` there.
- Engine stays policy-light; the module/user dual-auth mirrors `ModuleAccess`.
- Secret compared in constant time; never logged.
- Run Api test classes one at a time (EphemeralMongo/host-boot flakiness).
- Stage explicit file lists; do NOT commit in a worktree.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## Task 1: `AuditStamp.ViaModule` — carry the origin module on the entry

**Files:**
- Modify: `Backend/Accounting101.Ledger.Core/Journal/AuditStamp.cs` (add `string? ViaModule`)
- Modify: `Backend/Accounting101.Ledger.Mongo/Documents/JournalEntryDocument.cs` (persist + round-trip `ViaModule`)
- Modify: wherever the entry's audit is surfaced in `GET /audit/{entryId}` (the audit-log/actor projection) so the response carries `viaModule`
- Test: `Backend/Accounting101.Ledger.*.Tests` (round-trip + surfacing)

**Interfaces:** `AuditStamp` gains `public string? ViaModule { get; init; }` (the module key; null for a raw entry).

- [ ] **Step 1: Failing test** — construct an entry whose `AuditStamp.ViaModule = "payables"`, persist + read back via the Mongo store, assert it round-trips; and assert `GET /audit/{entryId}` (or the audit projection) exposes it. A raw entry has `ViaModule = null`.
- [ ] **Step 2: Run, confirm fail** (field doesn't exist / not persisted).
- [ ] **Step 3: Implement** — add the nullable init-only property to `AuditStamp`; map it in `JournalEntryDocument` to/from Mongo; include it in the audit read model. Do NOT change any posting logic yet (it's set by Task 3); default null everywhere so existing entries/tests are unaffected.
- [ ] **Step 4: Run, confirm pass** — new tests green; run the Core + Mongo + audit test classes to confirm no regression (the new field is additive/nullable).
- [ ] **Step 5: Build clean, commit** (`feat(core): AuditStamp carries the originating module (viaModule)`).

---

## Task 2: Module credential — secret on the registration + credential-verifying authenticator

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ModuleRegistration.cs` (add `Secret`)
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs` (`AddModule` issues/stamps the secret; make it available to the module for its client)
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs` (persist the secret on `RegisterModuleAsync`; lookup unchanged)
- Create: a credential-verifying `IModuleAuthenticator` (e.g. `Backend/Accounting101.Ledger.Api/Auth/CredentialModuleAuthenticator.cs`) — request-scoped, reads `X-Module-Key` + `X-Module-Secret` from the current request, looks up the registration, constant-time-compares the secret, returns the `ModuleIdentity` or null.
- Test: `Backend/Accounting101.Ledger.Api.Tests/`

**Interfaces:**
- Produces: `ModuleRegistration.Secret` (string); a request-scoped `IModuleAuthenticator` that yields a `ModuleIdentity` from valid `X-Module-Key`/`X-Module-Secret` headers (else null). Keep `HostStampedModuleAuthenticator` for the in-process document-store path — this new authenticator is for the **HTTP posting** path; if both must coexist, register the credential one for the request pipeline (resolve the design so the document store keeps its host-stamped identity and the posting path uses the credential one — implementer's call, documented in the report).

- [ ] **Step 1: Failing tests** — (a) a request with valid `X-Module-Key=payables` + matching secret → `Authenticate()` returns the `payables` identity; (b) wrong secret → null; (c) absent headers → null; (d) the secret is compared constant-time (use a fixed-time comparer; assert behavior, not timing).
- [ ] **Step 2: Run, confirm fail.**
- [ ] **Step 3: Implement** — add `Secret` to `ModuleRegistration` + persist it; at `AddModule`, generate a strong per-module secret if none exists (or accept one from configuration), store it via `RegisterModuleAsync`, and make it available to the module's DI scope (so its `HttpLedgerClient` can send it in Task 4). Implement the credential-verifying authenticator using `IHttpContextAccessor` + `ControlStore.GetModuleAsync` + a constant-time compare (`CryptographicOperations.FixedTimeEquals`). Never log the secret.
- [ ] **Step 4: Run, confirm pass** — new tests green; existing `ModuleHostingTests`/`DocumentStore*Tests` still green (the host-stamped doc-store path is untouched).
- [ ] **Step 5: Build clean, commit** (`feat(host): per-module credential + credential-verifying module authenticator`).

---

## Task 3: Gateway module-posting branch — authorize the module, stamp the user, record viaModule

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs` (add a module-aware posting resolution)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`PostEntry` uses it; `MapEntry` sets `AuditStamp.ViaModule`)
- Modify: reuse `ModuleAccess` (or its dual-auth shape) for the module's registered+enabled+member check
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModulePostingTests.cs` (create)

**Interfaces:**
- Consumes: Task 2's credential authenticator + `ModuleAccess`; Task 1's `AuditStamp.ViaModule`.
- The gateway resolves `PostEntry` as: **if a module identity is established** (credential valid) → authorize the module (registered + enabled) AND the user is a **member** of the client (membership only — not the user's `Post`); `Actor` = the user; carry the module key so `MapEntry` sets `ViaModule`. **Else** → the existing user-`Post` path, `ViaModule = null`.

- [ ] **Step 1: Failing tests** (`ModulePostingTests`):
  - a user who is a **member but lacks `Post`** posts WITH valid `X-Module-Key/Secret` → **201**, entry `CreatedBy = user`, `ViaModule = "payables"`.
  - same user posts WITHOUT module headers → **403** (no `Post`) — proves the module path is what authorized it.
  - a `Post`-holding user (controller) posts WITHOUT module headers → **201**, `ViaModule = null` (raw path unchanged).
  - valid module headers but the **module is disabled/unregistered** → refused (dual-auth).
  - the actor is always the token bearer regardless of headers (no impersonation).
- [ ] **Step 2: Run, confirm fail.**
- [ ] **Step 3: Implement** — add the module-posting resolution to `LedgerGateway` (branch on whether the credential authenticator established a module). Wire `PostEntry` to use it; thread the resolved module key into `MapEntry` so the built entry's `AuditStamp.ViaModule` is set (null on the raw path). Reuse `ModuleAccess` for the module half + the existing membership check for the user half. Keep the freeze/idempotency/validation logic exactly as-is.
- [ ] **Step 4: Run, confirm pass** — `ModulePostingTests` green; run `PostingValidationTests`, `IdempotentPostTests`, `PeriodCloseApiTests`, `CommandQueryTests`, `StructuredValidationErrorsTests` — all green (raw path unchanged).
- [ ] **Step 5: Build clean, commit** (`feat(ledger): module-authorized posting path (actor=user, viaModule=module)`).

---

## Task 4: Payables posts via the credential — end-to-end proof

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs` (`PostAsync` adds the module-credential headers from the module's injected secret)
- Modify: Payables module wiring if needed (inject the secret into the client)
- Test: `Modules/Payables/Accounting101.Payables.Tests/`

**Interfaces:** Consumes Task 2's per-module secret (injected) + Task 3's gateway path.

- [ ] **Step 1: Failing test** — enter a bill through Payables (real host); assert the resulting **engine entry carries `ViaModule = "payables"`** (read it back via the audit/entry projection). Today it would be null.
- [ ] **Step 2: Run, confirm fail** (client doesn't send the credential yet → raw path → null).
- [ ] **Step 3: Implement** — the Payables `HttpLedgerClient.PostAsync` attaches `X-Module-Key: payables` + `X-Module-Secret: <injected>` alongside the forwarded user token. Inject the module's secret into the client (from Task 2's wiring). Keep `ApproveAsync`/`ReverseAsync`/`VoidAsync` forwarding the user token as today (those are not "post-new-entry"; only `PostAsync` needs the credential — or apply consistently if the engine path requires it; implementer documents the choice).
- [ ] **Step 4: Run, confirm pass** — the new test green; re-run the full `Accounting101.Payables.Tests` (bills, payments, settlement) to confirm no regression — entries now carry `viaModule="payables"` but everything else is identical.
- [ ] **Step 5: Build clean, commit** (`feat(payables): post engine entries under the module credential`).

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: `ModulePostingTests`, the AuditStamp round-trip test, `PostingValidationTests`, `IdempotentPostTests`, `PeriodCloseApiTests`, `ModuleHostingTests`, `DocumentStore*Tests`, and the Payables suite — all green.
- [ ] Confirm: a member-without-`Post` posts via Payables credential (201, viaModule set) but is 403 raw; a controller posts raw (201, viaModule null); the doc-store host-stamped path is untouched; the module can't impersonate a user.
- [ ] Whole-branch review on the most capable model (an auth-surface change), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: viaModule (T1), credential + verifying authenticator (T2), module-authorized gateway path + actor stamping (T3), end-to-end module proof (T4). Additive: every task defaults to null/raw and asserts the raw path unchanged.
- Open implementer checks: (a) coexistence of the host-stamped (doc-store) and credential (posting) authenticators (T2 Step 3); (b) secret issuance/persistence mechanism — generate-on-register vs config (T2 Step 3); (c) whether non-Post engine calls (approve/reverse) also need the credential or stay user-forwarded (T4 Step 3).
