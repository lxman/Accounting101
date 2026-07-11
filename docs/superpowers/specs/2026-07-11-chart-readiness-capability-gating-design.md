# Chart-Readiness Capability Gating — Design

*2026-07-11. Closes the deferred parity fast-follow (#7) from the chart-readiness epic: the 6 advisory readiness endpoints enforce client membership but skip the per-module authorization every other module call applies. This gates them on the caller's per-module **read capability** (with an admin bypass) — deliberately NOT on `EnabledModules`, so previewing a not-yet-enabled module during onboarding still works.*

## Goal

A user may retrieve module *M*'s `GET /clients/{id}/{M}/chart-readiness` **iff** they are a deployment admin, OR a client admin, OR they hold *M*'s read capability. This closes the info-parity gap (a member with no payroll access can currently read payroll's chart config) without breaking readiness's onboarding purpose.

## The rule (single source of truth)

```
allow(moduleKey, caps) =
      caps.deploymentAdmin
   || caps.capabilities.contains("admin.client")        // client admin
   || caps.capabilities.contains(readCapFor(moduleKey)) // {area}.read
```

- `readCapFor`: `receivables→ar.read`, `payables→ap.read`, `payroll→payroll.read`, `cash→cash.read`, `fixedassets→fixedassets.read`, `inventory→inventory.read`. (Reconciliation is excluded — it has no readiness endpoint.)
- **Independent of `EnabledModules`** — a member with `payroll.read` may preview payroll readiness even before payroll is enabled. This is the onboarding path and is a first-class requirement, not an oversight.
- Non-members are denied upstream: `GET /me/capabilities` returns **403** for a non-member, which the module relays.

## Why this shape

Readiness reads the **shared chart** (`GetAccountsAsync`, bearer-forwarded) and runs the ModuleKit checker locally; it never makes a module-credentialed engine call, so it never reaches `ModuleAccess.AuthorizeAsync`. `ModuleAccess` bundles three gates — module-enabled (`EnabledModules`), membership, and capability. We want the **capability** gate (parity) but explicitly **not** the entitlement gate (which would break onboarding). So rather than route readiness through `ModuleAccess`, we make a lightweight capability decision from the caller's already-resolved capabilities.

## Architecture

**Zero engine change.** The engine already exposes `GET /clients/{id}/me/capabilities` (RequireAuthorization; resolves the caller's membership server-side; 403 for non-members) returning `CapabilitiesResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin)`. Each readiness handler fetches this (bearer forwarded) and applies the rule.

### Component 1 — ModuleKit `ReadinessAccess` (domain-safe assembly)

`Modules/Shared/Accounting101.ModuleKit/ReadinessAccess.cs`
- A pure decision + the module→read-capability map. Signature keeps ModuleKit decoupled from the exact DTO:
  ```csharp
  public static class ReadinessAccess
  {
      // moduleKey → the "{area}.read" capability; null for an unknown key.
      public static string? ReadCapabilityFor(string moduleKey);
      // deploymentAdmin OR holds admin.client OR holds ReadCapabilityFor(moduleKey).
      public static bool Allows(string moduleKey, bool deploymentAdmin, IReadOnlyCollection<string> capabilities);
  }
  ```
- `admin.client` is a private const in this class (the client-admin signal).
- An unknown/unmapped `moduleKey` with no admin ⇒ **deny** (fail-closed).

### Component 2 — `ModuleLedgerClient.GetMyCapabilitiesAsync` (ModuleKit.Api)

`Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClient.cs`
- New base method mirroring `GetAccountsAsync`: `GET clients/{clientId}/me/capabilities` via the existing `Forwarded(...)` (bearer only, no module credential), `EnsureSuccessAsync` (so a 403 for a non-member surfaces as `LedgerClientException(403)` and relays), returns `CapabilitiesResponse`.
- Added to each of the 6 module `ILedgerClient` interfaces (the inherited base method satisfies it — same pattern `GetAccountsAsync` used). Each module's hand-rolled test `FakeLedgerClient` gains a stub (matching the established `GetAccountsAsync` fake convention).
- `CapabilitiesResponse` is a `Accounting101.Ledger.Contracts` DTO (already shared; ModuleKit references Contracts).

### Component 3 — the 6 readiness handlers

Each handler (FA, Inventory, AR, AP, Cash, Payroll) gains a gate **before** the chart read, reusing the module-key literal it already passes to `ChartReadinessChecker.Check`:
```csharp
CapabilitiesResponse caps = await ledger.GetMyCapabilitiesAsync(clientId, ct); // non-member → relayed 403
if (!ReadinessAccess.Allows("cash", caps.DeploymentAdmin, caps.Capabilities))
    return Results.Problem("Not authorized to view this module's chart readiness.", statusCode: StatusCodes.Status403Forbidden);
// unchanged: GetAccountsAsync + ChartReadinessChecker.Check
```

### Component 4 — the dashboard widget

`UI/Angular/src/app/features/dashboard/chart-health-widget.ts` + `core/chart-health/chart-health.ts`
- The widget already loads the user's capabilities (`CapabilityService`: `deploymentAdmin`, `has(cap)`). It filters `CHART_HEALTH_MODULES` to the visible set — `deploymentAdmin || has('admin.client') || has(readCapFor(key))` — and only requests readiness for those. Admins see all 6.
- Add a `readCap` (the `{area}.read` string) to each `CHART_HEALTH_MODULES` entry so the filter and the server agree. The `X / N ready` summary reflects the visible count.
- Removes the 403 / "couldn't check" noise a limited user would otherwise see.

## Testing

- **ModuleKit unit** (`ReadinessAccess`): the full matrix — admin bypass (deploymentAdmin true; `admin.client` present) grants any module incl. unknown-key; `{area}.read` present grants that module only; absent ⇒ deny; each of the 6 mappings; unknown key without admin ⇒ deny.
- **ModuleLedgerClient**: `GetMyCapabilitiesAsync` calls `/me/capabilities`, forwards Authorization, deserializes `CapabilitiesResponse`; 403 → `LedgerClientException(403)`.
- **Per-module E2E** (representative: Cash + one of AR/Payroll to prove a non-key-matching area map like `ar.read`): member with the read cap → 200; member without it → 403; deployment admin without the cap → 200; a client whose `EnabledModules` does **not** include the module, member holding the cap → **200** (proves onboarding preserved). Keep every module's existing "authorized → 200" ChartReadinessE2e green (the seed fixture user is authorized).
- **Frontend** (widget spec): a user with a subset of caps sees only those modules (and the count reflects it); an admin (deploymentAdmin) sees all 6.
- **Dev-stack smoke** (mandatory before merge): switch *Acting as* between a broad identity (admin/all-caps → all 6 shown) and a limited identity → only permitted modules shown; a gated module returns 403 when hit directly. NOTE: both stock dev identities are broad (Dev Approver is `admin=true`), so the plan must provision or grant a **limited** dev identity/membership to actually exercise the deny path.

## Out of scope / non-goals

- No `EnabledModules` entitlement gate on readiness (deliberate — preserves onboarding).
- No routing readiness through `ModuleAccess.AuthorizeAsync` (it bundles the entitlement gate we're excluding; a lightweight capability decision is the right tool).
- No change to the readiness report shape, the checker, or any other module endpoint.
- Reconciliation (no readiness endpoint) is untouched.

## Execution

**REQUIRED SUB-SKILL:** Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans`. Fresh implementer per task, task review after each, final whole-branch opus review. Sonnet for the multi-file wiring/integration tasks; cheapest tier for the transcription-heavy ModuleKit/unit task; opus for the final review.
