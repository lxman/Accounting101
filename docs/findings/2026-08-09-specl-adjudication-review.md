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

## Finding 1 — approve-after-void is permitted (verified; actionable)

**Sequence:** `Post → Void → Approve` succeeds end to end.

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

## Finding 2 — no maker-checker identity check anywhere in the evidence (design decision, not a bug)

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
