# Module Visibility & Enablement (UI-side entitlement seam)

**Date:** 2026-07-11
**Status:** Design (pending user review)
**Type:** Product feature — frontend module visibility + backend enablement surface + entitlement seam

## Problem

Dog-fooding the app against a real core-only client (JordanSoft LLC — Cash + Bank Reconciliation only) surfaced a support-call-grade UX failure:

- The sidebar lists **every** subledger module (Receivables, Payables, Payroll, Fixed Assets, Inventory) regardless of whether the client has them. Clicking any of them hits the module-entitlement wall and returns **403** — a dead page reached through normal navigation.
- The dashboard's **Chart Health** widget queries all six modules' `chart-readiness` endpoints; for the five un-configured modules those **500**, rendering as alarming red **"couldn't check"** tiles.

This violates a principle already written into the system: *the read/nav filter is the primary UX; the command-side 403 is a defense-in-depth backstop that must be **unreachable through normal UI** — it only fires for stale or malicious calls.* A user clicking a visible nav link into a 403 is exactly what that principle forbids.

**Root cause:** the UI gates navigation by *capability area* (`hasArea('ar')` etc.) and has **no knowledge of which modules are enabled** for the client. `/clients/{id}/me/capabilities` returns capabilities + roles + `deploymentAdmin`, but not the client's `EnabledModules`. So an admin (who holds every capability area) sees every module, enabled or not.

**Secondary insight (from brainstorming):** Chart Health is really a *setup-time diagnostic* ("your chart isn't configured for a module you turned on — fix it here"), mis-placed as a permanent daily-dashboard fixture. "Which modules do I have?" is the nav's job, not a widget's.

## The layered model (context; most of it is future)

Module access decomposes into three layers, `entitled ⊇ enabled ⊇ authorized`:

| Layer | What it answers | Local (today) | On-prem licensed | SaaS on Azure |
|-------|-----------------|---------------|------------------|---------------|
| **Entitlement** | What a firm *may* use | none — all allowed | signed license file (vendor) | operator console per firm (+ billing) |
| **Enablement** | Turn on + configure | firm admin | customer's firm admin | each firm's admin |
| **Authorization** | Use it | user caps | user caps | user caps |

The enforcement plumbing for enablement already exists (`EnabledModules` per client, default-closed at the single `ModuleAccess.AuthorizeAsync` chokepoint; firm tier + suspension from the multi-firm tenancy epic). **This spec builds the enablement→UI wiring and the *entitlement seam* above it** — nothing more. The license/console/billing sources that fill the entitlement layer are explicitly future work (see Future Seams).

## Scope

**In scope (the "now-slice"):**

1. **Expose enabled modules to the UI** — extend the `/me/capabilities` response with `enabledModules`.
2. **Filter the nav** — a subledger module's nav item shows iff `hasArea(area) && moduleEnabled(key)`. No dead links → the 403 becomes unreachable through normal UI.
3. **Remove the Chart Health widget from the dashboard.**
4. **A firm-admin "Module Setup" screen** (under Administration) — lists *available* modules (from the entitlement seam), toggles enablement (existing `PUT /admin/clients/{id}/modules`), and shows each module's chart-readiness gaps inline with deep-links to fix. This is where the displaced Chart Health value lives.
5. **Exception-only gap surfacing** — a calm badge on an *enabled* module's nav item when its chart has a gap; silent when healthy.
6. **Readiness graceful degradation** — an un-configured module's `chart-readiness` returns a `200` advisory ("not configured"), never a `500` — honoring the endpoint's own "advisory, always 200" contract. (Defense-in-depth; also makes the setup-screen preview clean.)
7. **`admin.modules` capability** — gates the Module Setup screen and the enablement write, so a firm admin (not only a deployment admin) can activate modules.

**The entitlement seam:** the Module Setup screen's *available* set comes from an entitlement source (`IModuleEntitlement`), and enablement is bounded `enabled ⊆ available`. The default implementation returns **all** module keys (local/unbounded). License- and console-backed implementations plug in later without touching the enablement or nav code.

**Out of scope (future, seamed — see Future Seams):** the on-prem signed-license mechanism, the SaaS platform-operator console UI + per-firm entitlement layer, and billing-driven auto-suspend.

## Design

### Backend

- **`CapabilitiesResponse` += `enabledModules: IReadOnlyList<string>`.** `GetMyCapabilities` additionally reads the client's `EnabledModules` from the control store and includes it. This is the seam the UI consumes — one call it already makes, already re-resolving on client/identity change. No new endpoint for nav gating.
- **Readiness graceful path.** The per-module `chart-readiness` handler currently throws when a module's `*__Accounts__*` config is absent (`ConfiguredXAccountsProvider.Read` → `InvalidOperationException` → 500). Wrap the requirements-build so a missing configuration yields a `200` report with a `NotConfigured` status (report-level or per-account), not a 500. Keeps the advisory contract and lets the setup screen render "not configured yet" calmly.
- **`admin.modules` capability** added to the capability catalog. `PUT /admin/clients/{id}/modules` accepts this capability in addition to deployment admin, and validates `enabled ⊆ available` via the entitlement seam.
- **`IModuleEntitlement` seam** — `AvailableModulesAsync(firm/client)` returning the entitled module keys. Default `UnboundedModuleEntitlement` returns all known module keys. (Later: `LicensedModuleEntitlement` reads a signed license; `ConsoleModuleEntitlement` reads per-firm entitlement set by the operator console.)
- **Module Setup read endpoint** — returns `{ available[], enabled[], perModuleReadiness }` for the client, so the setup screen renders availability, current state, and gaps in one fetch. Reuses the existing readiness logic.

### Frontend

- **`CapabilitiesResponse` type += `enabledModules`.** `CapabilityService` exposes `enabledModules(): Signal<ReadonlySet<string>>` and `moduleEnabled(key): boolean`.
- **Nav (`nav.ts`).** For each subledger module item, gate on `hasArea(area) && moduleEnabled(moduleKey)` using an explicit nav-area→module-key map. Core groups (Overview, General Ledger, Assurance, Administration) are unchanged — they gate by capability only, as today.
- **Dashboard.** Remove the Chart Health widget and its wiring. (The `ChartHealthService` + readiness endpoints remain — reused by the Module Setup screen.)
- **Module Setup screen** (`features/admin/module-setup`, routed under Administration, guarded by `admin.modules`). Lists available modules with an enable/disable toggle; for each enabled module shows its readiness gaps with the existing deep-link-to-Chart-of-Accounts affordance. Non-available modules render as "not licensed" (the entitlement seam decides).
- **Exception-only nav badge.** An enabled module's nav item shows a small warning badge when its readiness is not-ready (fetched lazily/periodically via the existing `ChartHealthService`). No badge when ready — no noise.

### Result: the 403 principle restored

Once the nav shows only enabled modules, a normal user cannot navigate to a non-enabled module. The `ModuleAccess.AuthorizeAsync` 403 returns to being a pure backstop for stale/malicious calls — never reachable through the UI. The dashboard shows no readiness widget; a core-only client sees a clean nav and no red.

## Testing

- **Backend:** `/me/capabilities` includes `enabledModules` for a member; readiness on an unconfigured module returns `200` NotConfigured (not 500); `PUT .../modules` is allowed for `admin.modules` and rejects a module outside the available set (`enabled ⊆ available`); default entitlement returns all keys.
- **Frontend:** nav hides a subledger area whose module is not enabled even when the user holds its capability; the dashboard renders no Chart Health widget; the Module Setup screen reflects available/enabled/readiness and toggles enablement; the nav badge appears only for an enabled module with a gap.

## Success criteria

- A core-only (Cash) client sees **no** AR/AP/Payroll/FA/Inventory nav links, **no** dashboard readiness widget, and **no** reachable 403 through normal navigation.
- A firm admin can enable/disable an available module and see/fix its chart gaps in one screen.
- `chart-readiness` never returns 500 for an unconfigured module.
- Enablement is bounded by an entitlement seam that today returns all modules — so license/console/billing plug in later as a swap, not a rewrite.

## Future seams (explicitly out of scope)

- **`IModuleEntitlement` implementations:** `LicensedModuleEntitlement` (on-prem, verifies a vendor-signed license offline) and `ConsoleModuleEntitlement` (SaaS, reads per-firm entitlement written by the operator console).
- **Platform-operator console UI** — a frontend for the existing `/platform/*` endpoints to set per-firm entitlement and suspend firms for non-payment.
- **Billing → entitlement automation** — non-payment auto-suspends; the console is the manual override. (Deferred billing subsystem.)
