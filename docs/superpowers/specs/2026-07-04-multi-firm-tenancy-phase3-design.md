# Multi-Firm Tenancy — Phase 3 Design (platform operator tier, provisioning, entitlement, metering)

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)
**Builds on:** `2026-07-04-multi-firm-atlas-tenancy-design.md` (umbrella spec); Phases 1 (`0793267`) + 2 (`8177ecb`) + cleanups (`17093d3`) shipped.

## Problem

Phases 1–2 made the system multi-firm-capable: a firm registry, cluster factory, and a firm-scoped control plane where every request resolves a firm (claim or default) and cross-firm data access is structurally impossible. But there is still **no way to create a firm** (only the startup-seeded default firm exists), no **platform-operator** surface to manage firms/clusters, and the **per-client module entitlement** field (`EnabledModules`, added in Phase 1) is not yet enforced or settable. Phase 3 adds the operable control plane on top of the mechanism.

## Resolved decisions

- **Operator auth:** `platform=true` is a **token claim** enforced by a `PlatformAdmin` policy — exactly like today's `admin=true` firm-admin claim, one tier up. The trusted IdP (DevToken for now) issues it only to operators. Consistent with the existing model; production hardening lives in IdP config.
- **DB naming:** GUID-based — `firm_{firmId:N}_control`, `firm_{firmId:N}_client_{clientId:N}` — with the existing `Name` field serving humans. Matches what Phases 1–2 already build; zero new machinery.
- **Module entitlement is default-CLOSED:** an empty `EnabledModules` means **no modules entitled**. A client reaches a module's endpoints only if that module's key is in its `EnabledModules`.
- **Provisioning creates no membership.** (Corrects the umbrella spec.) Memberships are per-client; no client exists at firm-creation time, and firm-admin is the `admin=true` claim issued by the IdP — not a membership. Provisioning creates the firm + seeds its control DB; the IdP-issued `admin=true`+`firmId` token then drives client/member creation through the existing firm-scoped `/admin/*` endpoints.

## Scope & decomposition

Two independently-shippable sub-phases, built in order:

- **Phase 3a — platform operator tier + firm provisioning + cluster management.** The `/platform/*` control-plane surface.
- **Phase 3b — module entitlement (setter + chokepoint enforcement) + usage/metering read.**

**Out of scope (future):** the payment/billing subsystem that consumes the meter (Stripe, dunning, invoices, proration); real IdP integration; per-firm module *registration* (Phase 2's `ModuleRegistrar` seeds the default firm only — a new firm's modules register when it's provisioned, handled in 3a's provisioning or deferred if not needed for the demo).

---

## Phase 3a — operator tier, provisioning, clusters

### Platform operator tier
- `PlatformAdmin` authorization policy: `RequireClaim("platform", "true")`. Registered in `AddLedgerEngine` alongside the existing `AdminEndpoints.Policy`.
- A new `PlatformEndpoints` group mapped at `/platform`, `RequireAuthorization(PlatformAdmin)`.
- Platform handlers operate on `platform_control` via the **existing singleton `PlatformStore`** — they do NOT read `FirmScope`. `FirmResolutionMiddleware` still runs on these requests; an operator token with no `firm` claim resolves the always-present default firm harmlessly (no middleware exemption needed).

### Firm provisioning
- `POST /platform/firms` — body `{ name, clusterKey? }` (clusterKey defaults to `"default"`). Steps:
  1. Generate `firmId`; `controlDatabase = "firm_" + firmId.ToString("N") + "_control"`.
  2. `PlatformStore.RegisterFirmAsync` (Status=Active, CreatedUtc).
  3. Resolve the firm's cluster client via `IMongoClientFactory.GetAsync(clusterKey)`, build a `ControlStore` on the new control DB, and call `SeedBuiltinCapabilitySetsAsync` (so the firm is immediately usable by an `admin=true` firm admin).
  4. Return `201` with the firm id + control DB name.
  - Validation: reject an unknown `clusterKey` (the factory throws → map to 400); reject a blank name.
- `GET /platform/firms` — list all firms (id, name, status, clusterKey, controlDatabase, createdUtc).
- `PATCH /platform/firms/{firmId}/status` — body `{ status }` (`Active|Suspended`) → `PlatformStore.SetFirmStatusAsync`. Suspension takes effect at `FirmResolutionMiddleware` (already enforced: suspended firm → 403).

### Cluster management
- `POST /platform/clusters` — body `{ key, connectionString }` → `PlatformStore.RegisterClusterAsync`.
- `GET /platform/clusters` — list registered clusters (key + a redacted connection-string indicator; do **not** return raw connection strings).

### Testing (3a)
- Provisioning creates the control DB and seeds capability sets: a freshly provisioned firm's control DB has the built-in sets; a firm-admin-scoped token for that firm can immediately create a client.
- `PlatformAdmin` gate: a non-`platform` token gets 403 on every `/platform/*` route; a `platform=true` token is admitted.
- Suspend flow end-to-end: provision a firm, suspend it, a request carrying that firm's claim now 403s at the middleware.
- Cluster register/list; unknown-cluster provisioning → 400; blank name → 400.
- Cluster list never leaks raw connection strings.

---

## Phase 3b — module entitlement + usage

### Entitlement setter
- `PUT /admin/clients/{clientId}/modules` — body `{ moduleKeys: string[] }` → sets `ClientRegistration.EnabledModules` on the firm-scoped control DB. Gated by the per-client admin path (deployment/firm admin OR the `admin.users`-style capability, matching the existing `/admin/clients/{clientId}/...` endpoints). Idempotent replace.
- The **demo client** is backfilled by calling this endpoint once with its actual modules — the only production data to clean up.

### Chokepoint enforcement (default-closed)
- `ModuleAccess.AuthorizeAsync` gains one check: after the membership/capability checks, read the client via `ControlStore.GetClientAsync(clientId)` and verify `client.EnabledModules` contains `caller.Key`. If not → a new `ModuleAccessDecision.NotEntitled` → 403 at the boundary.
- **Default-closed:** an empty `EnabledModules` denies every module. There is no grandfather path.
- **Ordering:** entitlement is a property of the client/firm (is this module *sold* to this client), so it is checked *before or alongside* the user capability check — final ordering pinned in the plan. All refusals remain 403.

### Test-fixture consequence (part of 3b, not optional)
Because enforcement is default-closed, every test fixture that seeds a client which then exercises a module must set `EnabledModules` to include that module's key. Affected: the five module host fixtures' client-seed helpers (`ReceivablesHostFixture.SeedClientAsync`/`SeedSodClientAsync`, and the Payables/Payroll/Cash/Reconciliation equivalents), `ApiFixture`'s module-exercising seeds, and the direct-construction document-store fixtures/tests. Without this, the module E2E suites would fail with `NotEntitled` 403s. This is the 3b analog of Phase 2's fixture sweep.

### Usage / metering read
- `GET /platform/usage` (PlatformAdmin) — for each firm, open its control DB and return: firm id/name, count of `Active` clients, and a per-module-key count of clients that have it enabled. A snapshot the future billing subsystem consumes; no pricing logic here.

### Testing (3b)
- Enforcement: a client with `EnabledModules=[]` gets 403 (`NotEntitled`) on a module endpoint; after `PUT .../modules` enabling that module, the same call succeeds. The raw ledger path (no module) is unaffected.
- The entitlement check is firm-scoped (reads the caller firm's client registry) — a cross-firm clientId can't be entitled through another firm.
- Setter validation + idempotence; unknown module keys accepted or rejected per the pinned rule (decided in the plan).
- Usage read tallies active clients + enabled-module counts correctly across ≥2 firms; gated by `PlatformAdmin`.

## Components (new/changed)

- `Auth`/`Hosting`: `PlatformAdmin` policy constant + registration.
- `Endpoints/PlatformEndpoints.cs` (new): firms + clusters + usage handlers, `PlatformAdmin`-gated.
- `Endpoints/AdminEndpoints.cs` (or a small `MemberEndpoints` sibling): the `PUT .../clients/{clientId}/modules` setter.
- `Control/ModuleAccess.cs`: `NotEntitled` decision + the `EnabledModules` check.
- `ControlStore`: a `SetClientModulesAsync` (or reuse `RegisterClientAsync` with the field set) + whatever the usage read needs.
- Contracts: `ProvisionFirmRequest`/`FirmResponse`, `RegisterClusterRequest`/`ClusterResponse` (redacted), `SetClientModulesRequest`, `UsageResponse`.
- Test fixtures: client-seed helpers set `EnabledModules` (3b).

## Testing strategy (overall)

Follow the established pattern: EphemeralMongo via `SharedMongo`, GUID-isolated DBs, real HTTP through `WebApplicationFactory<Program>`. Each sub-phase ends green; 3b's fixture sweep is the atomic point where the module suites go green again under default-closed enforcement.

## Open questions

- **Unknown module keys in the setter** — accept freely (a key with no installed module is inert) or validate against installed `ModuleRegistration`s (reject unknown)? Lean: validate against installed modules for a helpful 400. Pin in the 3b plan.
- **Per-firm module registration on provisioning** — a newly provisioned firm's control DB has capability sets but no module registrations (`ModuleRegistrar` only seeds the default firm). For the single-demo-firm reality this doesn't bite; if a provisioned firm must use modules, provisioning should also seed module registrations into its control DB. Decide in 3a based on whether provisioned firms need modules now.
