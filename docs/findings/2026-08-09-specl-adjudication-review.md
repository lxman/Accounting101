# Specl adjudication review — engine state machine and approval findings

**Date:** 2026-08-09
**Source:** First live run of `specl check` (the spec-language toolchain's AI-adjudicated
checker) against the ledger engine. An unratified generic double-entry spec was judged
against ~163KB of engine source (Core + Contracts whole; the posting slice of Mongo:
LedgerService, MongoJournalStore, MongoBalanceProjection, ChartService,
JournalEntryDocument; policy files from Api: ApprovalPolicyEndpoints, Authorization,
Capabilities). Judge: `claude -p --model sonnet`, adversarial brief per statement.
Verdicts below marked **verified** were confirmed by hand against the source afterward;
the judge's raw rationale artifacts live in the specLang repo under `.specl/adjudications/`.

Caveat on provenance: the spec was written as a toy example for the spec-language design
doc, not drafted from this engine's domain. Verdicts that only reflected that mismatch
(the spec's `Pending/Posted/Void` single-status model vs. the engine's orthogonal
`LifecycleStatus × PostingState`, no supersede/revise in the spec) are recorded as
non-findings at the bottom. Only the items below survived filtering.

---

## Finding 1 — approve-after-void is permitted (verified; FIXED 2026-08-09)

**Status: RESOLVED.** `JournalEntry.Approve()` now guards `Status == Active` before the
`Posting` guard, mirroring `Void()` and `Supersede()`. Because the guard keys on the
lifecycle axis rather than on "voided" specifically, it closes the whole cross-axis family
in one stroke — a *superseded* pending entry (`Supersede()` likewise leaves
`Posting == PendingApproval`) was equally approvable before and is now rejected too.
`LedgerService.ApproveAsync` calls `entry.Approve()` before any persistence, so the domain
guard protects the service path as well; no separate service-layer check was needed.
Regressions: `LifecycleTransitionTests.Approve_rejects_a_voided_pending_entry` (Core) and
`LedgerServiceTests.Approving_a_voided_pending_entry_is_rejected_and_records_no_approval`
(Mongo, asserts the persisted entry stays Voided/PendingApproval with no `ApprovedBy` and no
appended `Approved` audit record). Full Core (55) + Mongo (105) suites green.

**Sequence (original defect):** `Post → Void → Approve` succeeded end to end.

- `JournalEntry.Void()` (`Backend/Accounting101.Ledger.Core/Journal/JournalEntry.cs:152-159`)
  requires `Status == Active`, sets `Status = Voided`, and leaves `Posting` untouched —
  a voided pending entry keeps `Posting = PendingApproval`.
- `JournalEntry.Approve()` (`JournalEntry.cs:137-149`) guards **only**
  `Posting != PendingApproval`. It never checks `Status`.
- `LedgerService.ApproveAsync` (`Backend/Accounting101.Ledger.Mongo/LedgerService.cs:117-148`)
  checks the **superseded original's** status on the revision path but never the entry's
  own `Status` before calling `entry.Approve(...)`.

**Effect:** a voided entry can be "approved": it gets `ApprovedBy` stamped into its
audit block, a version bump, and an `AuditAction.Approved` record appended to the
hash-chained audit log — an approval event recorded against a voided entry.

**Contained — no balance impact.** Both derivation paths filter on
`LedgerReplay.IsOnBooks` (`Status == Active && Posting == Posted`,
`Backend/Accounting101.Ledger.Core/Journal/LedgerReplay.cs:12-13`): replay ignores the
entry, and `MongoBalanceProjection.UpdateAsync` no-ops on it
(`MongoBalanceProjection.cs:47-48`). Defense-in-depth held. The damage is confined to
state-machine and audit-trail integrity, not money.

**Suggested fix:** add a `Status == Active` guard to `JournalEntry.Approve()` (mirror of
the guard `Void()` and `Supersede()` already carry), plus a regression test:
post → void → approve throws. Belongs to the same family as the deferred freeze-race
review item — a cross-field guard gap between the two orthogonal status axes.

## Finding 2 — no maker-checker identity check anywhere in the evidence (RESOLVED by clarification 2026-08-09)

**Status: NON-ISSUE — the enforcement exists; it was outside the evidence set.** The
maker≠checker *identity* check lives exactly where the architecture says it should: the host
approval-policy layer, in `ApproveEntry` at
`Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs:315-322`. Under
`ApprovalMode.TwoPerson` it returns 403 when `entry.Audit.CreatedBy == ctx.Actor.UserId`,
and it covers revisions and reversals too (all approved through that same endpoint). It is
tested by `ApprovalModeEnforcementTests.Two_person_forbids_the_author_approving_their_own_entry`
(403) and `Two_person_allows_a_different_approver` (200). The judge concluded "no home" only
because the evidence subset fed to `specl check` included `ApprovalPolicyEndpoints`,
`Authorization`, and `Capabilities` but **not** `LedgerEndpoints.cs` — where the check
actually is. Under `SelfApprove` self-approval is intentional; under `AutoApprove` no human
approves. Decision (2026-08-09): keep it in host approval policy, gated by `ApprovalMode` —
which is the current implementation. No code change. Lesson for future `specl` runs: include
the endpoint that performs the action, not just the policy/capability files, or the adjudicator
will report a control it cannot see as missing.

Original judge writeup follows (retained for context):

---



The engine records `ApprovedBy` but never compares it to `CreatedBy`; neither
`JournalEntry.Approve` nor `LedgerService.ApproveAsync` enforces actor distinctness.
The upstream policy layer in the evidence enforces role *separation* (Clerk cannot
approve; Approver cannot post) but not actor *identity*: any principal holding both
`gl.post` and `gl.approve` (Controller, Admin) can create an entry and approve it
themselves.

Per the project's working agreement, SoD policy deliberately lives upstream of the
engine — so the engine-level absence is by design. The open question this finding
sharpens: **the upstream layer included in evidence doesn't enforce it either.** If
self-approval-prevention is a requirement, it currently has no home. Decide where it
lives (host approval policy seems the natural place per the architecture) or record
explicitly that dual-capability self-approval is accepted for small shops.

Note the judge's own discrimination here, worth preserving: it marked the maker-checker
*rule* VIOLATED (the check demonstrably doesn't exist) but the self-approval *scenario*
INCONCLUSIVE (role configuration can prevent it in deployments that split the roles).
Both readings are correct at once.

## Non-findings (recorded so they aren't re-litigated)

- **Balance invariant: strongly satisfied.** The judge's rationale is a good compressed
  audit of the design: `JournalEntry` is only constructible via `Create` (private
  constructor) which throws `UnbalancedEntryException` on nonzero signed sum; every
  mutation is a `with`-copy touching only status/version/audit fields, never `Lines`;
  and the Mongo round-trip reconstructs through `Create`, re-validating balance on every
  read. Unbalanced-post rejection likewise satisfied at construction time, before any
  persistence.
- **Posting-transition and posted-is-final "violations": spec-model mismatch, no
  action.** The toy spec has a single three-state status and no supersede/revise
  concept; the engine's revision path (`Supersede`/`ReviseAsync`/`ApproveAsync` swap) is
  deliberate design the spec simply doesn't describe. A real spec for this engine would
  be drafted from its own vocabulary (`JournalEntry`, `LifecycleStatus`,
  `PostingState`, supersede, reversal) and ratified before its verdicts carry weight.
