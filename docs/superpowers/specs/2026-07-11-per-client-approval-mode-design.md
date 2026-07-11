# Per-Client Approval Mode — Design

**Date:** 2026-07-11
**Status:** Approved (brainstorm complete), pending implementation plan

## Summary

Replace the per-client boolean `RequireSegregationOfDuties` with a single per-client
enum `ApprovalMode { TwoPerson, SelfApprove, AutoApprove }`. This subsumes the existing
segregation-of-duties (SoD) policy and adds a third posture — **auto-approve** — for shops
that want entries to hit the books at post time without a distinct approval step, while
still recording a full audit trail.

Modelling this as one enum (rather than two booleans, e.g. `RequireSod` + `AutoApprove`)
makes the one illegal combination — SoD **on** *and* auto-approve **on** — **unrepresentable**.
Auto-approve inherently makes author = approver, which contradicts SoD; a single enum
cannot express both at once.

## Motivation

Today every client runs one of two postures via `RequireSegregationOfDuties`:

- `true` → an entry must be approved by someone **other** than its author (maker–checker).
- `false` → the author may approve their own entries (self-approve).

Real single-operator shops (e.g. JordanSoft LLC, the first live client — a sole proprietor
doing cash-basis journal entries) do not want a separate approval click at all. Entries post
`PendingApproval` and require a second call to reach the statements, which is pure friction
when there is only one person. **Auto-approve** removes that step: an entry is approved inline
at post time, by the posting actor — but the system **still writes both the post and the
approval audit events**, so the evidentiary chain is identical to a manual approval. Nothing
is lost from the audit record; only the human round-trip is removed.

## The three modes

| Mode | Semantics | Replaces |
|------|-----------|----------|
| **TwoPerson** | Author ≠ approver. SoD enforced at the approve endpoint. | `RequireSegregationOfDuties = true` |
| **SelfApprove** | Author may approve their own entries. | `RequireSegregationOfDuties = false` |
| **AutoApprove** | Entry approved inline at post time by the posting actor. Both post + approval audit events written. | *(new)* |

The modes apply to journal entries **and** to AR/AP Issue/Enter (invoice/bill promotion),
wherever the approval gate currently sits.

## Data model

### Enum (1-based, with a legacy sentinel)

```csharp
public enum ApprovalMode
{
    Unspecified = 0,   // legacy sentinel — the field was never stored on this document
    TwoPerson   = 1,
    SelfApprove = 2,
    AutoApprove = 3,
}
```

`Unspecified = 0` is deliberate: it is the default an old MongoDB document (persisted before
this field existed) deserializes to. This mirrors the existing `FiscalYearEndMonth` convention,
where legacy documents deserialize to `0` and readers normalize via `FiscalYear.MonthOf`.

### Storage

`ClientRegistration` (control DB) gains:

```csharp
public ApprovalMode ApprovalMode { get; set; }
```

The existing `RequireSegregationOfDuties` property **remains on the storage class** — but only
as a **read-only deserialization source for legacy documents**. It is never written again.
`ApprovalMode` is the sole authority going forward.

### Normalizer (lazy migration — no backfill job)

A single helper resolves the effective mode, mirroring `FiscalYear.MonthOf`:

```csharp
public static class ApprovalPolicy
{
    public static ApprovalMode ModeOf(ClientRegistration registration) =>
        registration.ApprovalMode != ApprovalMode.Unspecified
            ? registration.ApprovalMode
            : registration.RequireSegregationOfDuties
                ? ApprovalMode.TwoPerson
                : ApprovalMode.SelfApprove;
}
```

Legacy documents (including the live JordanSoft client) resolve correctly on read; there is
**no migration batch job**. Any new client is created with an explicit `ApprovalMode`
(default `TwoPerson`), so `Unspecified` only ever appears on pre-existing documents.

## Wire contract (DTOs)

`CreateClientRequest` and `ClientRegistrationResponse` (`AdminContracts.cs`) **carry
`ApprovalMode` and drop `RequireSegregationOfDuties`** — a clean swap, one representation on
the wire. The only callers are our own Angular UI and the test suite; both are updated. This
avoids re-introducing on-the-wire two-representation drift, which is exactly what the single
enum exists to prevent.

`CreateClientRequest.ApprovalMode` defaults to `TwoPerson` (the safe, most-restrictive
posture) when a client is seeded or created without specifying it. Because an omitted enum
deserializes to `Unspecified` (0), the create handler **explicitly maps `Unspecified →
TwoPerson`** at creation, so a newly created client never persists the legacy sentinel.

## Enforcement

### TwoPerson / SelfApprove — the approve endpoint

The one existing SoD guard in `ApproveEntry` (`LedgerEndpoints.cs:286`) reroutes from the
boolean to the normalizer:

```csharp
ApprovalMode mode = ApprovalPolicy.ModeOf(client);
if (mode == ApprovalMode.TwoPerson && entry.Audit.CreatedBy == ctx.Actor.UserId)
    return Results.Problem(/* SoD 403 */);
```

- `TwoPerson` → author ≠ approver enforced (unchanged behavior).
- `SelfApprove` → guard skipped; author may approve (unchanged behavior).

### AutoApprove — the post handlers

AutoApprove is enforced at the **post** path, not the approve endpoint. After an entry is
posted, when `ApprovalPolicy.ModeOf(client) == AutoApprove`, the host immediately approves the
just-posted entry inline, using the **posting actor**, so both the post audit event and the
approval audit event are recorded (identical evidentiary chain to a manual approval).

This hook must cover **both** journal-entry post entry points:
- single-entry post
- `PostBatchAsync` / `POST /entries/batch`

and the **AR/AP Issue/Enter** promotion paths, wherever they currently create a
`PendingApproval` entry.

The SoD guard on the approve endpoint is unaffected by AutoApprove (auto-approved entries
never reach a manual approval call in the normal flow; a manual approve of an already-approved
entry remains a no-op/conflict as today).

## Authorization

Changing a client's approval posture can **weaken** an internal control (turning off SoD), so
it is treated as a sensitive single lever rather than folded into general client admin — matching
the existing precedent of the seeded **"Fiscal Admin"** and **"Posting-Accounts Admin"** narrow
capability sets, each of which carries exactly one `admin.*` cap plus `gl.read`.

New capability:

```csharp
public const string AdminApprovalPolicy = "admin.approvalPolicy";
```

Wiring:
- Added to `Capabilities.All`.
- Added to the `LedgerRole.Admin` preset (a full Admin has it).
- A seeded **"Approval Policy Admin"** narrow capability set = `[admin.approvalPolicy, gl.read]`,
  following the Fiscal / Posting-Accounts pattern in `ControlStore`.
- The GET/PUT approval-policy endpoints are gated by `AdminApprovalPolicy` via
  `AdminAuthorization.MayAsync` (deployment `admin=true` continues to override, as with all
  admin caps).

## API surface

Two per-client endpoints (active-client scoped, like the other admin surfaces):

- `GET  /clients/{clientId}/approval-policy` → `{ mode: ApprovalMode }` (resolved via normalizer).
- `PUT  /clients/{clientId}/approval-policy` — body `{ mode: ApprovalMode }`; gated
  `admin.approvalPolicy`. Rejects `Unspecified` (422) — a client cannot be *set* to the legacy
  sentinel.

`ApprovalMode` is also settable at creation via `CreateClientRequest.ApprovalMode`.

## UI

There is **no client create/edit UI today** — clients are created via seed scripts / direct
API. The existing admin screens (`admin/users`, `admin/access/sets`) are per-client and operate
on the **active client** from the client switcher, following a **one-screen-per-admin-concern**
grain (the pending Module Setup screen also gets its own route).

ApprovalMode follows that grain:

- **Route:** `admin/approval-policy`, gated by an `admin.approvalPolicy` capability guard,
  in the **Administration** nav section, scoped to the active client.
- **Control:** a vertical **radio group** — one row per mode, each with a bold label and a
  one-line description. **AutoApprove is visually flagged as the low-control option** (it removes
  a review step). No cross-guarding of options: because the three modes *are* the SoD setting
  (one enum), selecting AutoApprove simply moves the client out of TwoPerson — there is no
  illegal combination to disable.
- **Behavior:** load current mode via GET, save via PUT. Success/error toast; optimistic or
  reload per existing admin-screen convention.

### Why radios, not a 3-position switch

Each mode needs a sentence to be understood (especially AutoApprove's "requires no second
review, still audited"). A segmented/3-position switch has nowhere to carry per-option
description and cannot visually flag the low-control option. Radios read as a deliberate
governance choice rather than a light toggle, which matches a per-client internal-control
decision.

## Scope boundaries (YAGNI)

This feature does **not** build out the firm-admin or super-admin (platform-operator) UI
console shells. Those tiers exist in the backend (`admin.firm` capability — currently a defined
-but-unenforced placeholder; `platform=true` claim → `/platform/*`), but a proper tier→screen
build-out is a **separate epic**. ApprovalMode is one per-client setting on the existing
per-client admin surface; a firm or deployment admin who outranks the client admin can already
edit it when scoped to that client. Building three admin consoles to host one enum field would
be badly disproportionate.

Likewise, this feature does **not** construct the full "which capabilities does each admin
tier need" list. That list does not exist today (the single `Admin` role preset grabs all five
`admin.*` caps at once; `admin.firm` and `admin.postingAccounts` are defined but not enforced
at any endpoint). Articulating that per-tier list is a prerequisite for the admin-console epic,
not for this feature — ApprovalMode needs exactly one gating decision (`admin.approvalPolicy`),
which this spec makes.

## Testing

- **Normalizer:** `ApprovalPolicy.ModeOf` — stored mode wins when set; legacy bool `true→TwoPerson`,
  `false→SelfApprove`; `Unspecified` only from legacy docs.
- **Enforcement — TwoPerson:** author approving own entry → 403 (unchanged); other approver → OK.
- **Enforcement — SelfApprove:** author approving own entry → OK.
- **Enforcement — AutoApprove:** posted entry lands `Approved`; **both** post and approval audit
  events present, both stamped with the posting actor; covers single post, batch post, and AR/AP
  Issue/Enter.
- **Authorization:** `PUT /approval-policy` — holder of `admin.approvalPolicy` succeeds; a plain
  clerk 403s; deployment admin overrides; `Unspecified` body → 422.
- **Wire:** `CreateClientRequest` / `ClientRegistrationResponse` serialize `ApprovalMode` as the
  expected representation (guard against enum-as-number vs string wire mismatch — the recurring
  UI-mock casing trap); no `RequireSegregationOfDuties` on the wire.
- **UI:** approval-policy screen — RED-first component spec; renders three radios, loads current
  mode, PUTs the chosen mode; guarded by `admin.approvalPolicy`.
- **Dev-stack smoke:** flip a client to AutoApprove, post an entry, confirm it reaches the trial
  balance without a manual approval and the audit log shows both events (the only layer that sees
  real serialization).
