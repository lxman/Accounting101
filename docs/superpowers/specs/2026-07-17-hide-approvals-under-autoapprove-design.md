# Hide the Approvals queue under AutoApprove — design

**Date:** 2026-07-17
**Status:** Approved (design)
**Area:** General Ledger nav / Admin (approval policy)

## Goal

When the active client's approval mode is `AutoApprove`, hide the **Approvals**
nav leaf (`/journal/approvals`, the pending-entry queue) and redirect that route
to `/journal`. Under AutoApprove, entries reach the books at post time, so the
queue is permanently empty — dead UI. The **Approval policy** admin screen
(`/admin/approval-policy`) is unaffected (it's how the mode is changed back).

## Approach

The nav is filtered in `shell.ts` by a `canSee` predicate driven by
`CapabilityService`, which re-resolves `GET /clients/{id}/me/capabilities`
whenever the client or acting identity changes. The approval mode belongs in
that response — one source, already reactive at nav-render time — rather than a
second fetch.

## Backend

Add the mode to the capabilities response (additive, backward-compatible):

- `CapabilitiesResponse` (`AdminContracts.cs`): add `ApprovalMode ApprovalMode`.
- `GetMyCapabilities` (`CapabilitiesEndpoints.cs`): populate via
  `ApprovalPolicy.ModeOf(client)` — the handler already loads the
  `ClientRegistration`.
- Wire format: the global string-enum converter serializes it as
  `"approvalMode":"AutoApprove"` (confirmed by the fiscal-settings live smoke,
  where the sibling `ClientRegistrationResponse` returned the mode as a string).

## Frontend

### CapabilityService + model

- `CapabilitiesResponse` interface + `EMPTY_CAPABILITIES` (`capabilities.ts`): add
  `approvalMode: ApprovalMode`, reusing the existing
  `'TwoPerson' | 'SelfApprove' | 'AutoApprove'` union. Default in
  `EMPTY_CAPABILITIES` is `'TwoPerson'` — a non-AutoApprove default so nothing
  hides during load (empty caps already hide the leaf via area-gating).
- `CapabilityService`: add `readonly approvalMode: Signal<ApprovalMode>` from
  `current().approvalMode`.

### Nav model + shell predicate

- `NavLink` (`nav.ts`): add optional `hideWhenAutoApprove?: boolean`; set it
  `true` on the `/journal/approvals` leaf only.
- `shell.ts` `canSee` predicate: add one clause —
  `&& (!link.hideWhenAutoApprove || this.caps.approvalMode() !== 'AutoApprove')`.

### Route guard (the "+ route" depth)

- New functional guard `hideWhenAutoApproveGuard` (mirrors `canWrite`: waits for
  `caps.loaded`, then decides). On `/journal/approvals`: if
  `caps.approvalMode() === 'AutoApprove'` → `router.parseUrl('/journal')`, else
  `true`. Wired as `canActivate` on that route in `app.routes.ts`.

### Reactivity after a policy change

- After `ApprovalPolicyScreen.save()` succeeds, call `caps.reload()` (inject
  `CapabilityService`). Without it, switching to AutoApprove wouldn't hide the
  leaf until the next client/identity switch or reload; with it, the leaf
  disappears immediately.

## Testing

- **Backend:** assert `/me/capabilities` returns `approvalMode` (extend/add a
  `CapabilitiesEndpoints` test).
- **Frontend:**
  - shell spec — Approvals leaf hidden when `approvalMode==='AutoApprove'`, shown
    for other modes.
  - guard spec — redirects to `/journal` under AutoApprove, allows otherwise.
  - approval-policy spec — a successful save calls `caps.reload()`.
- **Dev-stack smoke:** JordanSoft is currently `AutoApprove` — the Approvals leaf
  should be gone and `/journal/approvals` should redirect; flipping to TwoPerson
  restores it.

## Out of scope

Posting/approval behavior; the entry-detail approve affordance (write-gated
separately); the Approval policy screen's own visibility.

## Files touched

**Backend**
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — `CapabilitiesResponse` field.
- `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs` — populate it.
- `Backend/Accounting101.Ledger.Api.Tests/...` — capabilities response test.

**Frontend**
- `UI/Angular/src/app/core/capabilities/capabilities.ts` — interface + default.
- `UI/Angular/src/app/core/capabilities/capability.service.ts` — `approvalMode` signal.
- `UI/Angular/src/app/layout/nav.ts` — `hideWhenAutoApprove` flag + set on Approvals leaf.
- `UI/Angular/src/app/layout/shell.ts` — predicate clause.
- `UI/Angular/src/app/core/capabilities/hide-when-autoapprove.guard.ts` — new guard.
- `UI/Angular/src/app/app.routes.ts` — wire the guard on `/journal/approvals`.
- `UI/Angular/src/app/features/admin/approval-policy.ts` — `caps.reload()` after save.
- Specs: `shell.spec.ts`, guard spec, `approval-policy.spec.ts`.
