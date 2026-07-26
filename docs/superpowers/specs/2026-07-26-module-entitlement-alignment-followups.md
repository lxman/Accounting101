# Module entitlement alignment — follow-ups left open at merge

**Date:** 2026-07-26
**Branch:** `feat/module-entitlement-alignment` (merged to master)
**Spec:** [2026-07-26-module-entitlement-alignment-design.md](2026-07-26-module-entitlement-alignment-design.md)

Everything the branch set out to do is done and verified. These are items surfaced *during* review
that were deliberately not fixed on this branch, recorded so they are not rediscovered from scratch.

## 1. `Accounting101.EInvoicing.Tests` is not in `Accounting101.slnx`

**Highest-value item here.** The private overlay's 77-test suite lives under
`Modules.Private/Accounting101.EInvoicing/` with its own `Accounting101.EInvoicing.slnx`, and is
absent from the main solution. Neither `dotnet build Accounting101.slnx` nor a
`Ledger.Api.Tests` run touches it.

That matters more than it looks. `Accounting101.EInvoicing.Api/EInvoicingEndpoints.cs:56-63` uses
`capabilities.EnabledModules.Contains("einvoicing")` — read from the capabilities payload — as its
**authorization gate**, and its own comment says that is the only place `EnabledModules` is enforced
for that module. This branch redefined what that payload contains. The suite was run manually during
the final fix wave (77/77 green, the intersection is a no-op there because the Host globs the overlay
in), but nothing automatic would have caught a regression.

The overlay is gitignored, so it cannot simply be added to the committed solution file. Options worth
weighing: a conditional `.slnf`/solution filter, a build-script step that runs the private suite when
the overlay is present, or accepting manual runs and documenting the trigger. Whatever is chosen, the
gate should be that a change to `CapabilitiesResponse` cannot merge without the private suite running.

## 2. Angular `auth.interceptor.spec.ts` fails (598/599)

Pre-existing and unrelated — verified with `git diff --stat master...HEAD -- UI/`, which is empty for
this branch. Traced to commit `7f3bbb2` (2026-07-02); the cause is a name mismatch between
`dev-identities.ts` and `environment.ts`. Untouched here because this branch changed no Angular file
at all, deliberately.

## 3. `InstalledModules` does not honour `ModuleRegistration.Enabled`

`InstalledModules` means *composed*, not *composed and permitted*: it selects every registration's key
unconditionally, while `ModuleAccess` refuses a module whose control-DB row has `Enabled == false`. So
a module disabled in the control DB would still appear in capabilities and still render nav — the same
nav-to-a-refusal failure this branch exists to eliminate, one layer up.

Not reachable today: no endpoint sets `Enabled`, and `ModuleRegistrar` re-upserts `true` at every boot.
A cross-reference comment now sits beside the `Enabled` check in `ModuleAccess` so whoever adds a
disable switch finds the coupling. If that switch is ever built, both places change together.

## 4. The write guard depends on caller discipline

`PUT /admin/clients/{id}/modules` is a full replace. The guard now carries forward already-stored
uninstalled entitlements (rejecting only *newly added* uninstalled keys), so an unrelated edit no
longer erases what the customer bought. But a caller that round-trips the **availability** list rather
than the raw one would still drop an uninstalled key — the API cannot distinguish that from a
deliberate removal.

Mitigated by design: `GET /admin/clients/{id}/modules` returns `ModuleKeys` raw and un-intersected
precisely so a caller has the full set to echo. Worth remembering if an admin module-editor UI is ever
built — it must bind to the raw list, never to capabilities.

## 5. Cosmetic

- The design spec's `ModuleAccess.cs` line anchors (lines 16 and 96) are stale by five lines: the
  cross-reference comment added to `ModuleAccess` shifted the entitlement check from `:59` to `:64`.
  Content is correct; only the anchors drifted.
- `SetClientModules`' 422 body names a duplicated absent key once per occurrence, since the message
  joins the unfiltered `Where` result. Pre-existing, harmless.
- `ClientRegistration.EnabledModules` has a public setter and an `= []` initializer; a hand-edited
  control-DB row with an explicit `null` would NRE in `SetClientModules` and in the pre-existing
  `ModuleAccess` check alike. Pre-existing shape, not introduced here.

## Explicitly out of scope, per the spec

The packaging discipline that makes self-hosted licensing real — which overlays a customer's build
gets, how a release is verified to contain only what was sold, and what stops an operator dropping a
DLL in afterwards. That is release engineering, it is a separate sub-project, and it should be
designed when self-hosted is closer. This branch made the application code honest; it does not claim
to make the DLL unobtainable.
