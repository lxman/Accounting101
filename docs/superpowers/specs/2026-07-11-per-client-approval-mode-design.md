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

As a small rider (same files, same review), this feature also removes the ghost `admin.firm`
capability — defined and bundled into the `Admin` preset but enforced nowhere. See *Ghost-cap
correction*.

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

The modes apply to **every entry-creation path** — direct journal entries, all subledger module
postings (AR Issue, AP Enter, payroll, cash, fixed-assets, inventory), revisions, and reversals
— because they all resolve through the shared host creation handlers. See *Enforcement* for the
exact surface.

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

### AutoApprove — the entry-creation handlers

AutoApprove is enforced at the **creation** path, in the host, not at the approve endpoint.
When `ApprovalPolicy.ModeOf(client) == AutoApprove`, immediately after a new pending entry is
created the host calls the existing engine `ApproveAsync(entryId, actor)` for it, using the
**creating actor**. Both the `Created` and the `Approved` audit events are therefore written,
with the same actor — an evidentiary chain identical to a manual approval; only the human
round-trip is removed. The handler returns the **post-approval** status (`Posted`), so a caller
(including a module) sees the entry's true final state.

Reusing `ApproveAsync` is deliberate: it already performs the revision supersede-swap and the
reversal application correctly, so all creation kinds are auto-approved through one
well-tested path rather than duplicated logic.

**Complete surface — the four host handlers that create a `PendingApproval` entry:**

| Handler | Route | Engine call |
|---------|-------|-------------|
| `PostEntry` | `POST /entries` | `PostAsync` |
| `PostBatch` | `POST /entries/batch` | `PostBatchAsync` (approve each written entry) |
| `ReviseEntry` | `POST /entries/{id}/revise` | `ReviseAsync` |
| `ReverseEntry` | `POST /entries/{id}/reverse` | `ReverseAsync` |

**Modules are covered transitively — no per-module code.** Every subledger (AR Issue, AP Enter,
payroll runs, cash, fixed-assets, inventory) posts through `PostEntry` / `PostBatch` via
`gateway.ResolveForPostAsync(..., moduleAuth, ...)` (the entry is stamped `Audit.ViaModule`).
Because AutoApprove lives in those two shared handlers, an AutoApprove client's module postings
are auto-approved automatically. No module host changes.

**Atomicity.** The auto-approve is a second transaction after the create (the engine stays
policy-agnostic — it receives no `ApprovalMode`), so a process crash in the narrow window
between create and approve would leave an `Active`/`PendingApproval` entry. This is a **benign,
self-healing transient**, not corruption: the entry is durable and simply awaits approval. The
auto-approve step also runs on the **idempotent-replay** branch when the replayed entry is still
pending, so a retried post heals a straggler. (If this window ever proves material, the
hardening path is to thread a host-computed `autoApprove` flag into the engine create methods so
create+approve share one transaction — deferred; not needed for v1.)

**SoD guard interaction.** The approve-endpoint SoD guard (TwoPerson) is unaffected: AutoApprove
approves via the engine directly, bypassing the endpoint guard, which is correct because
AutoApprove is by definition author = approver. A manual approve of an already-auto-approved
entry remains a no-op/conflict as today.

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

### Ghost-cap correction: remove `admin.firm`

While editing `Capabilities.cs` and `RolePresets.cs` to add `admin.approvalPolicy`, this feature
also **removes the `admin.firm` capability**. It is a ghost: defined in the vocabulary and bundled
into the `Admin` role preset, but **enforced at no endpoint, granted by no seeded set, used by no
UI, and asserted by no functional test** — there is literally nothing for it to gate (firm
provisioning is a platform-operator concern gated by `platform=true`, not `admin.firm`; a prior
design doc records that `admin.firm` endpoints do not exist). An unenforced, grantable admin cap
is a latent confusion/security smell — someone can grant it believing it confers authority.

Removal touches exactly three lines: the `AdminFirm` const, its entry in `Capabilities.All`, and
its entry in the `LedgerRole.Admin` preset. Any test that asserts the exact contents/count of
`Capabilities.All` or the `Admin` preset is updated. Stored grants of the string `"admin.firm"`
(if any exist in a DB) simply become unrecognized and inert; new grants of it are rejected by
`All`-based validation, which is the desired outcome.

When the firm-admin console epic lands and there is a firm-scoped surface to protect, it
reintroduces a **properly enforced** `admin.firm`. Carrying dead vocabulary until then buys
nothing.

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
console shells. The super-admin tier exists in the backend (`platform=true` claim →
`/platform/*`); the firm-admin tier is currently only a tenancy brick with no admin surface (and
this feature removes its ghost `admin.firm` cap — see *Ghost-cap correction*). A proper
tier→screen build-out is a **separate epic**. ApprovalMode is one per-client setting on the
existing per-client admin surface; a firm or deployment admin who outranks the client admin can
already edit it when scoped to that client. Building admin consoles to host one enum field would
be badly disproportionate.

Likewise, this feature does **not** construct the full "which capabilities does each admin
tier need" list. That list does not exist today (the single `Admin` role preset grabs all the
`admin.*` caps at once; `admin.postingAccounts` is defined and seeded as a narrow set but not yet
enforced at any endpoint — left as-is, out of scope). Articulating that per-tier list is a
prerequisite for the admin-console epic, not for this feature — ApprovalMode needs exactly one
gating decision (`admin.approvalPolicy`), which this spec makes.

## Testing

- **Normalizer:** `ApprovalPolicy.ModeOf` — stored mode wins when set; legacy bool `true→TwoPerson`,
  `false→SelfApprove`; `Unspecified` only from legacy docs.
- **Enforcement — TwoPerson:** author approving own entry → 403 (unchanged); other approver → OK.
- **Enforcement — SelfApprove:** author approving own entry → OK.
- **Enforcement — AutoApprove:** created entry lands `Posted`; **both** `Created` and `Approved`
  audit events present, both stamped with the creating actor; the handler response reports the
  post-approval (`Posted`) status. Covered across **all four creation handlers** — single post,
  batch post, revise (supersede swap applied), reverse (reversal applied) — **and transitively via
  a module posting** (a `ViaModule`-stamped `POST /entries` under AutoApprove lands `Posted`,
  proving no per-module code is required). Idempotent-replay of a still-pending entry heals it to
  `Posted`.
- **Authorization:** `PUT /approval-policy` — holder of `admin.approvalPolicy` succeeds; a plain
  clerk 403s; deployment admin overrides; `Unspecified` body → 422.
- **Wire:** `CreateClientRequest` / `ClientRegistrationResponse` serialize `ApprovalMode` as the
  expected representation (guard against enum-as-number vs string wire mismatch — the recurring
  UI-mock casing trap); no `RequireSegregationOfDuties` on the wire.
- **Ghost cap:** `Capabilities.All` no longer contains `admin.firm`; the `Admin` preset no longer
  grants it; existing preset/vocabulary assertions updated to match; a grant request for
  `"admin.firm"` is rejected as an unknown capability.
- **UI:** approval-policy screen — RED-first component spec; renders three radios, loads current
  mode, PUTs the chosen mode; guarded by `admin.approvalPolicy`.
- **Dev-stack smoke:** flip a client to AutoApprove, post an entry, confirm it reaches the trial
  balance without a manual approval and the audit log shows both events (the only layer that sees
  real serialization).
