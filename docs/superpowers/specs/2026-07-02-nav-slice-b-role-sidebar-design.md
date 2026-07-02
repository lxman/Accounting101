# Slice B — Role-Based Sidebar — Design

**Date:** 2026-07-02
**Status:** Approved (design)
**Parent:** `2026-07-01-role-based-nav-rbac-design.md` (umbrella). Sub-project B.
Slice 0 (grouped sidebar) and Slice A (backend capability foundation) shipped.
This slice makes the sidebar filter itself by the acting user's capabilities —
the headline "different nav per role" feature.

## Goal

Fetch the acting user's resolved capabilities from the Slice-A endpoint and hide
sidebar destinations the user has no capability for, re-resolving whenever the
client or the acting identity (the "Acting as" switcher) changes.

## Decisions (from brainstorming)

- **Sidebar visibility only.** Screen write-gating (auditor read-only vs clerk
  read/write on the same screen) is **Slice C**, not here.
- **Area-based visibility:** each nav link maps to a capability *area*; a link
  shows if the user holds any capability in that area. (Coarse by design; the
  read/write distinction is Slice C.)
- **Tight narrow-clerk default:** correct the four narrow-clerk presets to a tight
  read scope so per-role sidebars are genuinely different. Owner reconfiguration
  of any member's capability set is **Slice D** (the authority is already
  per-member from Slice A; only the admin UI is missing).
- **Administration** is gated by `admin.*` capabilities (the per-client Admin
  role), NOT the deployment-admin flag.

## Backend reality this builds on (Slice A)

- `GET /clients/{clientId}/me/capabilities` → `{ capabilities: string[], roles:
  string[], deploymentAdmin: bool }`, resolved server-side from the membership's
  capability set.
- Capabilities are `area.level` strings: areas `gl, ar, ap, payroll, cash,
  bankrec, fixedassets, audit, reports, admin`.
- `RolePresets` (`Backend/.../Control/RolePresets.cs`) define the grant-time
  bundles. The `gl.*` equivalence invariant (each of the 5 original presets' gl.*
  maps to that role's `RolePermissions`) must remain intact — this slice only
  touches subledger/assurance reads on the narrow clerks.

## Frontend reality this builds on

- `authInterceptor` attaches the active identity's `DevToken`
  (`DevIdentityService.active()`), so switching identity changes the token on the
  next request.
- `ClientContextService.clientId()` and `DevIdentityService.active()` are signals.
- Services build `${environment.apiBaseUrl}/clients/${clientId()}...`.
- `nav.ts` exports grouped `NAV: NavSection[]` (Slice 0). `shell.ts` renders `NAV`
  and consumes `NavStateService` (accordion state + `activePath`).

## Change 1 — Tight narrow-clerk presets (backend)

In `RolePresets.cs`, change the four narrow clerks from the current all-reads
bundle to a tight scope (gl.read + own-module read/write only):

| Role | New preset |
|------|-----------|
| ArClerk | `gl.read, ar.read, ar.write` |
| ApClerk | `gl.read, ap.read, ap.write` |
| PayrollClerk | `gl.read, payroll.read, payroll.write` |
| CashClerk | `gl.read, cash.read, cash.write, bankrec.read, bankrec.write` |

Auditor / Clerk / Approver / Controller / Admin presets are **unchanged** (broad
reads). The `gl.*` of every narrow clerk is still exactly `{gl.read}` → the
equivalence invariant and `CapabilityModelTests` still hold. Update the one
integration assertion in `CapabilitiesTests` that expected an AR clerk to hold
`ap.read` (now absent) → assert it is absent.

## Change 2 — CapabilityService (frontend)

`core/capabilities/capability.service.ts` — root singleton.

- Types: `CapabilitiesResponse { capabilities: string[]; roles: string[];
  deploymentAdmin: boolean }`.
- Reactively fetch: a `key = computed(() => clientId ? { clientId, sub } : null)`
  where `sub = identity.active().sub`; `toObservable(key)` → `switchMap` to
  `GET .../me/capabilities` (or `of(EMPTY)` when no client), `catchError` → EMPTY
  (a 403/None yields an empty set, i.e. nothing visible but no crash);
  `toSignal(..., { initialValue: EMPTY })`.
- Exposes: `capabilities: Signal<ReadonlySet<string>>`, `roles: Signal<string[]>`,
  `deploymentAdmin: Signal<boolean>`, plus `has(cap): boolean` and
  `hasArea(area): boolean` (any capability starting with `area + '.'`).
- `EMPTY = { capabilities: [], roles: [], deploymentAdmin: false }`.

Switching the acting identity re-emits `key` → re-fetches with the new token
(the interceptor reads the now-active identity at request time).

## Change 3 — Nav area binding + filtering

- Add `area?: string` to `NavLink`. Annotate every link in `nav.ts`:
  - Overview/Dashboard → *(none — always visible)*
  - General Ledger: all → `gl`
  - Subledgers: Receivables→`ar`, Payables→`ap`, Payroll→`payroll`, Cash & Banking→`cash`, Bank Reconciliation→`bankrec`, Fixed Assets→`fixedassets`
  - Assurance: Audit (+children)→`audit`, Reports (+Budgets)→`reports`
  - Administration: all → `admin`
- Add a pure helper to `nav.ts`:
  `visibleSections(nav: NavSection[], canSee: (area?: string) => boolean): NavSection[]`
  — keep items where `canSee(item.area)`, filter each item's `children` the same
  way, drop sections with no visible items. `canSee` for a link with no area
  returns true.
- `shell.ts`: inject `CapabilityService`; `visibleNav = computed(() =>
  visibleSections(NAV, a => !a || caps.hasArea(a)))`; render `visibleNav` instead
  of `NAV`. `NavStateService` continues to operate over the full `NAV` for
  `activePath`/`locate` (active detection is display-independent).

Result with the tight presets: an **AR Clerk** sees Overview + General Ledger +
Receivables only; an **Auditor/Approver/Controller** sees everything except
Administration; an **Admin** sees everything. A member with no membership (403 →
empty set) sees only Dashboard.

## Change 4 — Dev identity roster (for the switcher demo)

Move the dev identity roster into a committed constant so the switcher can offer
the full range of roles (subs are fixed non-secret GUIDs; only `devClientId`
stays local/uncommitted). `core/api/dev-identities.ts`:

```
Dev Controller  sub …0001  claims: role=Controller
Dev Approver    sub …0002  claims: role=Approver, admin=true
Dev Auditor     sub …0003  claims: role=Auditor
Dev AR Clerk    sub …0004  claims: role=ArClerk
Dev Admin       sub …0005  claims: role=Admin, admin=true
```

`DevIdentityService.identities` reads this constant (default active = the first).
Role claims are decorative (authority comes from the membership); `admin=true`
drives the deployment-admin flag. The three new subs get memberships seeded in
`.localdev` at finish (controller step). Existing subs …0001/…0002 keep their
seeded Controller/Approver memberships.

## Out of scope

- Screen/button write-gating (**Slice C**).
- Owner-facing capability/visibility configuration UI (**Slice D**).
- Any real (non-dev) auth.
- Backend enforcement of subledger/admin capabilities (**Slice E**).

## Testing & verification

- **Backend:** `CapabilityModelTests` unchanged-green; `CapabilitiesTests`
  AR-clerk assertion updated (ap.read absent). Full backend suite green.
- **Frontend (`npx ng test --watch=false`):**
  - `capability.service.spec`: fetches `.../me/capabilities` when client+identity
    set; `hasArea`/`has` reflect the response; switching identity re-fetches;
    no client → empty; 403 → empty set.
  - `nav.spec`: `visibleSections` filters correctly for representative predicates
    (ar-only → Overview+GL+Receivables; admin-inclusive → Administration present;
    none → Overview only).
  - `shell.spec`: with a stub `CapabilityService` (override the provider), the
    sidebar shows/hides the right sections; existing accordion tests get a
    full-capability stub so they behave as before.
  - `dev-identity.service.spec`: roster includes the five identities; `use()`
    switches active.
- **Smoke (finish):** seed the three new memberships in `.localdev`; start the
  stack; switch "Acting as" across Controller / Auditor / AR Clerk / Admin and
  confirm the sidebar changes (AR Clerk narrow; Admin shows Administration).

## Execution

One branch (`feature/nav-slice-b-role-sidebar`), subagent-driven. Four tasks:
(1) tighten narrow-clerk presets + fix backend test; (2) `CapabilityService` +
spec; (3) nav area binding + `visibleSections` + shell filtering + shell/nav spec
updates; (4) dev identity roster + spec. Then controller finish: seed memberships
+ smoke-test distinct sidebars.
