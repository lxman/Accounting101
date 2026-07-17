# Block AutoApprove while approvals are pending — design

**Date:** 2026-07-17
**Status:** Approved (design)
**Area:** Admin (approval policy) / General Ledger

## Goal

On the Approval policy screen, when the client has journal entries awaiting
approval (`PostingState.PendingApproval`), show a note and prevent selecting or
saving **AutoApprove** until the queue is cleared. Enforce the block on the
backend (the real guard); the frontend disables the option and explains why.

This complements the prior "hide Approvals under AutoApprove" feature: rather
than silently stranding a pending backlog when switching to AutoApprove, the
switch is blocked until the backlog is cleared.

## Backend

`ApprovalPolicyEndpoints` (which already gates both handlers on
`admin.approvalPolicy` via `AdminAuthorization.MayAsync`) gains a
`ClientLedgerFactory` injection and a helper to count pending entries. Because
authorization already happened at the endpoint, the count uses the factory
directly (no extra `gl.read`/membership coupling):

```
long pending = (await ledgers.CreateAsync(clientId, ct))?.Journal
    .CountByPostingAsync(clientId, PostingState.PendingApproval, ct) ?? 0;
```

- **`PUT /clients/{id}/approval-policy`:** after the existing `Unspecified`→422
  check and the `GetClientAsync` null→404 check, if `request.Mode == AutoApprove`
  **and** pending count > 0 → **422** (`ValidationProblem`/`Results.Problem`) with
  detail: *"Cannot enable auto-approve while N entr(y|ies) await approval. Clear
  the approval queue first."* Returns before persisting. Transitions to/from
  TwoPerson and SelfApprove are never blocked.
- **`GET /clients/{id}/approval-policy`:** response gains `PendingApprovalCount`
  so the screen renders the note without a second `gl.read` call.
- **Contract:** `ApprovalPolicyResponse(ApprovalMode Mode, long PendingApprovalCount)`.

### Count-access decision

Use `ClientLedgerFactory.CreateAsync(clientId)` directly (not
`gateway.ResolveAsync(Permission.Read)`), so the count does not depend on the
caller holding `gl.read`. The endpoint's `admin.approvalPolicy` gate is the
authorization boundary; the count is a server-side invariant read.

## Frontend

- **Model** (`core/approval-policy/approval-policy.ts`): `ApprovalPolicy` gains
  `pendingApprovalCount: number`.
- **Service** (`approval-policy.service.ts`): `get()` already returns the whole
  `ApprovalPolicy`; no signature change, the new field rides along. `set()`
  unchanged.
- **Screen** (`features/admin/approval-policy.ts`): store
  `pendingApprovalCount` from the load. When it is > 0:
  - The **AutoApprove** radio is **disabled** (`[disabled]` on the input;
    `select()` ignores it as a guard).
  - An inline note renders under the AutoApprove option: *"N entries are awaiting
    approval. Clear the approval queue before enabling auto-approve."* where
    "approval queue" is a `routerLink` to `/journal/approvals` (visible, since the
    client is not yet AutoApprove).
  - Singular/plural: "1 entry is" / "N entries are".
  - The save-path 422 detail is surfaced in the existing error banner as a race
    backstop (an entry posts between load and save).
- The count is fetched on screen load; navigating back after clearing the queue
  re-fetches it.

## Testing

- **Backend** (`ApprovalPolicyEndpointTests`):
  - `PUT`→AutoApprove with a pending entry present → 422, and `GET` still reports
    the prior mode (not persisted).
  - `PUT`→AutoApprove with zero pending → 200.
  - `PUT`→TwoPerson / SelfApprove with pending present → 200 (never blocked).
  - `GET` returns `PendingApprovalCount` matching the ledger.
- **Frontend** (`approval-policy.spec.ts`):
  - note shown + AutoApprove radio disabled when `pendingApprovalCount > 0`.
  - note absent + AutoApprove enabled when 0.
  - a 422 on save surfaces its `detail` in the error banner.
- **Dev-stack smoke:** flip JordanSoft to TwoPerson; post a pending entry; confirm
  the switch to AutoApprove is blocked (screen shows the note + disabled option;
  API returns 422); approve or void the entry; confirm the switch to AutoApprove
  now succeeds; restore AutoApprove.

## Out of scope

No change to how entries are approved; no auto-sweep of the backlog (the block is
the chosen behavior). The "hide Approvals under AutoApprove" behavior is unchanged.

## Files touched

**Backend**
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — `ApprovalPolicyResponse` gains `PendingApprovalCount`.
- `Backend/Accounting101.Ledger.Api/Endpoints/ApprovalPolicyEndpoints.cs` — inject factory, count helper, GET field, PUT guard.
- `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyEndpointTests.cs` — new tests.

**Frontend**
- `UI/Angular/src/app/core/approval-policy/approval-policy.ts` — model field.
- `UI/Angular/src/app/features/admin/approval-policy.ts` — note, disabled AutoApprove, count state, queue link.
- `UI/Angular/src/app/features/admin/approval-policy.spec.ts` — new tests.
