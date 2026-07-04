# Multi-Firm Tenancy — Module Secret Persistence Design

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)
**Builds on:** the multi-firm tenancy epic (Phases 1–3b + per-firm module registration `f1dba55`).

## Problem

Each installed module authenticates to the ledger over HTTP with a shared secret: the module *sends* `X-Module-Secret`, and `CredentialModuleAuthenticator` compares it (constant-time) against the `ModuleRegistration.Secret` stored in the request's firm-scoped control DB. Today that secret is generated **fresh on every process startup**, inside `AddModule` (`RandomNumberGenerator.GetBytes(32)`), and held only in-process (the module's `ModuleCredential`) and in whatever control DBs a given process seeded.

This is invisible for the shipped on-site reality (single firm, single process): every boot, the in-process secret and the default firm's control-DB copy are written from the same value in the same process, so they always match. But now that firm provisioning writes module registrations into *provisioned* firms' control DBs, and the epic targets a multi-firm SaaS, the per-boot regeneration is a release-blocking availability bug:

- **Single instance, across a restart:** a firm provisioned in run 1 has secret `S1` in its control DB; run 2 generates `S2`, re-seeds only the *default* firm with `S2`, and leaves the provisioned firm at `S1`. Its modules now send `S2`, the authenticator finds `S1`, the match fails, the request falls through to the raw path (no `Post` permission) → **403 on every module operation** until the firm is manually re-seeded. Restarts are routine in production.
- **Horizontal scaling (multiple instances):** broken immediately, even without restarts — each instance generates its own secret, and a request load-balanced to an instance other than the one that seeded a given control DB fails to match. Module auth is fundamentally single-instance as it stands.

The symptom is a silent, confusing partial outage: core GL (the raw ledger path) keeps working while every *module* operation — invoices, bills, payroll, bank rec — 403s after a deploy, with no module-config change to point at. It is an availability problem, not a confidentiality one (no secret leaks).

## Goal & non-goals

**Goal:** module secrets are **stable across process restarts and shared across instances**, so a module authenticates against any firm's control DB regardless of which process or instance handles the request or seeded the firm.

**Non-goals (YAGNI):**
- No deliberate secret-**rotation** capability (re-issuing a module's secret on demand and propagating it). Secrets are merely made stable; rotation is a separate future feature.
- No config/secret-store integration (env vars, AWS Secrets Manager). The generate-once-persist approach keeps the auto-generated, no-ops-config ergonomics; a secret-store backend can be added later behind the same seam.
- No change to the authorization model, the per-client `EnabledModules` entitlement gate, or the raw ledger path.

## Approach

Generate each module's secret **once**, persist it in `platform_control` (the process-independent, cross-firm, cross-instance registry that already holds firms and clusters), and **load** it on every startup instead of regenerating. Secret resolution moves from DI-wiring time (synchronous, no DB access) to a startup hosted service (async, DB reachable). The in-process `ModuleCredential` (what the module sends) and the `ModuleRegistration` singletons (what gets written to control DBs) are populated from the persisted value before any request is served or any firm is seeded.

Rejected alternatives:
- **Re-seed all firms on every boot** (keep per-boot regeneration, rewrite the new secret into every firm's control DB at startup): fixes the single-instance restart case only, does **not** fix horizontal scaling (instances still clobber each other, last-writer-wins), is O(firms) writes per boot, and is racy under concurrent instance startups.
- **Config/secret-store supplied secrets:** HA-safe but pushes per-module secret management onto ops and adds an external dependency; heavier than needed now and not precluded by this design.

## Components (new/changed)

### `ModuleSecret` document + `moduleSecrets` collection (platform_control)
A minimal record in the platform registry DB:
- `Key` (`[BsonId]`, the module key, e.g. `"receivables"`)
- `Secret` (the persisted Base64URL secret)

One document per installed module. Lives beside `firms` and `clusters` in `platform_control`; it is process- and instance-independent.

### `PlatformStore.GetOrCreateModuleSecretAsync(string key, Func<string> generate, CancellationToken)`
Atomic get-or-create:
1. Find the `moduleSecrets` document by key; if present, return its `Secret`.
2. Otherwise insert `{ _id: key, secret: generate() }`.
3. On an `E11000` duplicate-key error (a concurrent insert from another instance booting at the same time), re-read and return the winning document's secret.

The unique `_id` (module key) plus the E11000 re-read is what makes concurrent multi-instance startup converge on one secret. This mirrors the race-tolerant seed pattern already used by `PlatformClusterSeeder` / the Phase 1 startup path.

### `ModuleSecretResolver` (new `IHostedService`)
Runs on startup, **before `ModuleRegistrar`** (so the registrations it writes carry the resolved secret). For each installed module:
- resolves the persisted secret via `GetOrCreateModuleSecretAsync(key, GenerateSecret)`,
- sets the in-process `ModuleCredential.Secret` for that key (the outbound value the module's `HttpLedgerClient` sends), and
- sets the `ModuleRegistration.Secret` singleton (the value `ModuleRegistrar` and `ProvisionFirm` write into control DBs).

Ordering: registered in `AddLedgerEngine` (which runs before any `AddModule`, so it precedes the `ModuleRegistrar` that `AddModule` contributes) and after `AddPlatformRegistry` (so `PlatformStore` is available). It only touches `platform_control`, independent of firm existence.

### `AddModule` (changed)
Stops generating a per-boot random secret. Registers:
- the keyed `ModuleCredential` for the module with an **empty** secret (populated by the resolver at startup), and
- the `ModuleRegistration` singleton with `Enabled = true` and an **empty** secret (populated by the resolver).

The 32-byte→Base64URL generation helper moves out of `AddModule` and is invoked only inside the get-or-create path.

### `ModuleCredential` (changed shape)
Becomes a mutable holder: the `Key` stays immutable; the `Secret` is settable (default empty) so the resolver can populate it at startup. The module `HttpLedgerClient`s are **unchanged** — they still inject the keyed `ModuleCredential` and read `.Secret` at request time, by which point it is populated. (Requests are served only after all `IHostedService.StartAsync` complete, so the value is always present in time.)

### `ModuleRegistrar` and `ProvisionFirm` (unchanged)
Both already write the `ModuleRegistration` singletons into a control DB (default firm and provisioned firms respectively). Those singletons now carry the persisted secret, so every firm gets the stable value with no change to these call sites.

## Data flow

```
startup:
  ModuleSecretResolver.StartAsync
    for each installed ModuleRegistration:
      secret = PlatformStore.GetOrCreateModuleSecretAsync(key, GenerateSecret)   // platform_control
      ModuleCredential(key).Secret = secret        // outbound (module sends)
      ModuleRegistration.Secret     = secret        // control-DB record (written next)
  ModuleRegistrar.StartAsync
    control(default firm).SeedModulesAsync(registrations)   // default firm gets persisted secret

provisioning (request time, after startup):
  ProvisionFirm
    control(new firm).SeedModulesAsync(registrations)       // new firm gets same persisted secret

request time:
  module HttpLedgerClient sends X-Module-Secret = ModuleCredential.Secret (persisted)
  CredentialModuleAuthenticator compares against firm control DB's ModuleRegistration.Secret (persisted)
    → match, regardless of process / instance / when the firm was provisioned
```

## Error handling

- **Get-or-create race** (`E11000`): re-read and return the persisted winner — convergence, not failure.
- **platform_control unreachable at startup:** the resolver throws; the host fails fast, exactly as the existing startup seeders do. A half-started host that can't reach its registry should not serve traffic.
- **Empty secret at request time** (resolver didn't run, e.g. a misconfigured host): the constant-time comparison against an empty stored/sent secret fails to match → auth returns null → the module is treated as unauthenticated (fails **closed**). No bypass.
- No secret is ever logged or returned in a response (unchanged from today).

## Testing strategy

Follow the established pattern: EphemeralMongo via `SharedMongo`, GUID-isolated DBs, real HTTP through `WebApplicationFactory<Program>` where an end-to-end path is exercised.

- **`GetOrCreateModuleSecretAsync`:** create-then-read returns the same secret; a second call for the same key is idempotent (returns the first value, does not overwrite); a pre-inserted value is returned unchanged (race/idempotence tolerance).
- **Stability across reboots:** running the resolver twice against the same `platform_control` yields the **same** secret in the in-process credential + registration both times (the crux of the fix).
- **Cross-restart provisioned-firm auth (core regression):** boot host 1, provision firm B (secret `S` persisted); boot a **second** host against the same Mongo connection + same `PlatformDatabase`; assert host 2's resolved module secret equals firm B's stored control-DB secret — i.e. firm B's modules still authenticate after a restart. (Two `WebApplicationFactory` instances sharing connection string + `PlatformDatabase`.)
- **Regression:** the existing suite (333) stays green — each fixture's isolated `PlatformDatabase` gets its own resolved secrets, and the module E2E paths (`ModuleViaReceivablesTests`, `ModulePostingTests`) still authenticate because the resolver populates the credential before requests are served.

## Open questions

None. The approach, storage location (`platform_control`), and scope (stabilize only, no rotation) are pinned.
