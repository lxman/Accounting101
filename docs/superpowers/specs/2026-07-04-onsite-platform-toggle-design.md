# On-Site Platform-Surface Toggle Design (Phase 4)

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)
**Builds on:** the multi-firm tenancy epic (Phases 1–3b + per-firm module registration `f1dba55` + module secret persistence `83cc497`).

## Problem

The multi-firm machinery is compiled into one host. The platform-operator control plane — `/platform/*` (provision firms, register clusters, read the cross-firm usage meter) — is mapped **unconditionally** (`Program.cs`, `app.MapPlatformEndpoints()`), so it is exposed on every deployment, including on-site single-firm installs run by the customer themselves. An on-site deployment has exactly one firm (the default) and no operator tier; exposing an endpoint surface that can provision firms, register clusters, or enumerate usage is unnecessary attack surface it will never legitimately use.

Phase 4 collapses the deployment to on-site mode by default: the operator control-plane **surface** is off unless a SaaS-operator deployment explicitly turns it on.

## Resolved decisions

- **Default posture: OFF.** The platform surface is disabled unless a deployment sets `Tenancy:Platform:Enabled=true`. The powerful operator plane is opt-in (fail-safe); a SaaS operator deployment opts in, an on-site install does nothing.
- **Scope: disable the `/platform/*` surface only.** No additional single-firm guards. The single-firm invariant is already enforced by data: on-site, only the default firm exists in `platform_control`, so `FirmResolutionMiddleware` already 403s any non-default firm claim (`GetFirmAsync` → null). No new assertion earns its keep.
- **Toggle the surface, not the registry tier.** `AddPlatformRegistry` (the `PlatformStore`, cluster-keyed `IMongoClientFactory`, cluster/default-firm seeders, `FirmResolutionMiddleware`) stays unconditional — even on-site, the default firm lives in `platform_control` and every request resolves its control DB through the registry. Only the operator **endpoints** are gated.

## Approach

A boolean config flag `Tenancy:Platform:Enabled` (default `false`). `MapPlatformEndpoints` self-gates on it: when disabled it maps nothing, so `/platform/*` returns a standard 404 (the routes do not exist) and no operator plane is discoverable. Everything else — the ledger, `/admin/*`, modules, firm resolution, default-firm seeding — is unchanged.

Rejected alternative: a `Tenancy:Mode = OnSite | Saas` enum. More expressive if "mode" ever grows more axes, but YAGNI for a single boolean toggle today; a boolean is the smaller, clearer surface and an enum can replace it later without changing the call sites.

## Components (new/changed)

### `TenancyDefaults.PlatformEnabled(IConfiguration)` (new)
A one-line reader beside the existing `ResolveDefaultFirmId`:

```csharp
/// <summary>Whether the platform-operator control plane (/platform/*) is exposed. OFF by default — an
/// on-site single-firm deployment has no operator tier. A SaaS operator sets Tenancy:Platform:Enabled=true.</summary>
public static bool PlatformEnabled(IConfiguration configuration) =>
    bool.TryParse(configuration["Tenancy:Platform:Enabled"], out bool enabled) && enabled;
```

An unset, blank, or unparseable value yields `false` (the safe default).

### `PlatformEndpoints.MapPlatformEndpoints` (changed — self-gating)
Reads configuration from the endpoint builder's service provider and returns early when the platform surface is disabled, so nothing under `/platform` is mapped:

```csharp
public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
{
    if (!TenancyDefaults.PlatformEnabled(app.ServiceProvider.GetRequiredService<IConfiguration>()))
        return; // on-site: no operator control plane

    RouteGroupBuilder platform = app.MapGroup("/platform").RequireAuthorization(Policy);
    // ... existing firm / cluster / usage route mappings, unchanged ...
}
```

`Program.cs` keeps its single unconditional `app.MapPlatformEndpoints()` call; the gating lives with the surface it controls, so there is exactly one place that knows the rule and it is exercised by booting the host with the flag on or off.

### Untouched (explicitly)
- `FirmResolutionMiddleware`: its `/platform` prefix bypass is inert when the routes are unmapped (a `/platform/*` request 404s whether or not it bypassed firm resolution). No change.
- The `PlatformAdmin` authorization policy registration: harmless with no endpoints consuming it. No change.
- `AddPlatformRegistry` and every seeder: the registry tier always runs. No change.

## Data flow & error handling

- **Disabled (default):** `MapPlatformEndpoints` maps nothing → any `/platform/*` request (with or without an operator token) is an unrouted 404. No new error path; 404 leaks nothing about the disabled feature.
- **Enabled:** identical to today — `/platform/*` mapped, `PlatformAdmin`-gated (non-operator → 401/403, operator → handler).
- Firm resolution, default-firm seeding, and client/ledger access are unaffected in both modes.

## Test-fixture consequence (part of this work)

Because the default is now OFF, fixtures that exercise `/platform` must opt in with `Tenancy:Platform:Enabled=true`:

- `ApiFixture` (drives `PlatformFirmsTests`, `PlatformClustersTests`, `PlatformUsageTests`) sets the flag true.
- `ModuleSecretPersistenceTests`' own two-host `WebApplicationFactory` (it provisions a firm via `/platform/firms`) sets the flag true.

The five module `HostFixtures` are deliberately left at the default (off): the module suites then run in genuine on-site mode, proving modules, `/admin`, and the ledger work with no operator plane. No other fixture touches `/platform`.

## Testing

New `PlatformToggleTests` (Api.Tests), each building its own host with the flag set explicitly:

- **Disabled → platform routes are 404:** a host with `Tenancy:Platform:Enabled=false`; `GET /platform/firms` and `POST /platform/firms` return 404 even when carrying a valid operator (`platform=true`) token — the surface is absent, not merely forbidden.
- **Disabled → the rest of the app still routes:** on the same host, a non-platform control-plane route (e.g. `GET /admin/clients` without a token) returns 401 (routed, auth-challenged) — not 404 — proving only `/platform` was removed.
- **Enabled → platform routes are mapped:** a host with `Tenancy:Platform:Enabled=true`; `GET /platform/firms` with an operator token returns 200, confirming the toggle restores the surface.

Existing platform tests continue to pass once `ApiFixture` opts in. The whole solution stays green.

## Open questions

None. The flag name (`Tenancy:Platform:Enabled`), default (off), gating point (`MapPlatformEndpoints` self-gate), and scope (surface only) are pinned.
