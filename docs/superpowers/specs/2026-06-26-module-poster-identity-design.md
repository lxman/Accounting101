# Slice 1 — Module-poster identity + actor stamping — Design

**Date:** 2026-06-26
**Status:** Spec for review
**Umbrella:** [MVP Module Architecture](2026-06-26-mvp-module-architecture-design.md) — this is build-sequence slice 1 (the foundation).

## Goal

Let a module post a journal entry to the engine **authorized as the module**, while the engine records the **originating user (clerk) as the business actor**. Two independently-validated identities flow on a module-originated post: **who** (the user, from their validated token) and **which** (the module, from its own credential). Purely additive — existing user-token posting is unchanged; the clerk keeps `Post` (it's removed later, slice 6).

## What already exists (build on it)

- `IModuleAuthenticator.Authenticate() → ModuleIdentity?` — the seam, explicitly designed for "host-stamped in-process; **credential-verified out-of-process**." Today only `HostStampedModuleAuthenticator` (in-process DI) exists, feeding the **document store**.
- `ModuleRegistration { Key, Name, Enabled }` in the control DB; `ControlStore.GetModuleAsync(key)` / `RegisterModuleAsync`.
- `ModuleAccess.AuthorizeAsync(caller: ModuleIdentity, targetNamespace, userId, clientId)` — the **dual-auth template** (module registered+enabled+owns namespace AND user is a member), already used by `ScopedDocumentStore`.
- `LedgerGateway.ResolveAsync(user, clientId, Permission required)` — resolves the user → `Actor`, checks the user's role holds the permission. The single front door for posting.
- `AuditStamp { CreatedBy, CreatedAt, PostedBy?, ApprovedBy? }` — the entry's ownership stamp.

**The asymmetry slice 1 closes:** document-store access is in-process DI, so the `ModuleIdentity` is right there. **Posting goes over the loopback `HttpLedgerClient`, which forwards only the user token** — the module identity does not ride along, so the engine cannot tell a module post from a raw clerk post. Slice 1 makes the module *prove which it is* on the posting call (a credential), exactly as the `IModuleAuthenticator` abstraction anticipates.

## Mechanism (option 1 — module credential)

### 1. Module credential on the registration
`ModuleRegistration` gains a `Secret` (an opaque per-module shared secret, generated when the module is registered / stamped at host wiring). The control DB holds it; the module holds its own copy (in-process: injected at wiring alongside its `ModuleIdentity`; out-of-process: configuration). No user ever sees it.

### 2. The posting call carries both identities
A module's `HttpLedgerClient`, when it posts, sends:
- the forwarded **user bearer token** (unchanged — the actor), and
- its **module credential** as headers (`X-Module-Key: payables` + `X-Module-Secret: <secret>`).

### 3. The engine authenticates both, independently
On `POST /entries`:
- The user token is validated → `Actor` (the clerk). **The module never supplies the user identity** — it comes only from the engine validating the token, so a module cannot impersonate a user.
- If module-credential headers are present, a **credential-verifying `IModuleAuthenticator`** (new, request-scoped) looks up `ModuleRegistration` by key and constant-time-compares the secret → `ModuleIdentity` (or null/401 on mismatch).

### 4. Authorization flips to the module when module-originated
`LedgerGateway` gains a posting resolution that branches:
- **Module-originated** (valid module credential present): authorize the **module** — registered + enabled (reuse the `ModuleAccess` dual-auth shape) AND the user is a member of the client. The user's role need **not** hold `Post`. `Actor` = the user; record `ViaModule = module.Key`.
- **Raw** (no module credential): unchanged — authorize the user's `Permission.Post` as today. `ViaModule = null`.

### 5. Audit stamp carries the origin
`AuditStamp` gains `string? ViaModule` (the module key; null for a raw accountant entry). Persisted on the entry and surfaced in the audit log / `GET /audit/{entryId}`, so the trail reads "posted by user XXX **via the YYY module**."

## Scope

**In slice 1:** the `ModuleRegistration.Secret`, the credential-verifying `IModuleAuthenticator`, the gateway module-posting branch (dual-auth), `AuditStamp.ViaModule` (Core + Mongo persistence + audit-log surfacing), and the module `HttpLedgerClient` sending the credential headers. Prove it end-to-end by wiring **one existing module (Payables)** to post via the credential path and asserting the entry is authorized-as-module and stamped `viaModule="payables"`.

**NOT in slice 1 (later slices):** removing `Post` from the Clerk role (slice 6); migrating Receivables and the other modules (slice 5); the new Payroll / Cash modules (slices 2–3). The raw user-token posting path stays fully working.

## Testing

Engine (`Accounting101.Ledger.Api.Tests`, EphemeralMongo + WebApplicationFactory):
- **Module-credential post is authorized as the module + stamped with the actor:** a post with valid `X-Module-Key/Secret` by a user who is a **member but lacks `Post`** succeeds; the entry's audit shows `CreatedBy = user`, `ViaModule = "payables"`.
- **Bad/absent secret → not treated as a module:** wrong secret → 401/ignored; the call then falls to the raw path (and a non-`Post` user → 403). Proves the credential actually gates.
- **A user cannot forge the module path:** a direct call with a guessed `X-Module-Key` but no/wrong secret does not get module authorization.
- **Raw posting unchanged:** a `Post`-holding user (controller) posts with no module headers → 201, `ViaModule = null`.
- **The module never impersonates a user:** the actor on a module post is always the token bearer, regardless of headers.

Module (`Accounting101.Payables.Tests`): a bill entered through Payables posts and its engine entry carries `ViaModule="payables"` (the loopback client sends the credential).

## Global constraints
- .NET 10; build 0 warnings; commit per task; TDD.
- Purely additive — raw posting and existing behavior unchanged; the clerk keeps `Post` until slice 6.
- Engine stays policy-light; the module/user dual-auth reuses the existing `ModuleAccess` shape.
- Secret compared in constant time; never logged.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
