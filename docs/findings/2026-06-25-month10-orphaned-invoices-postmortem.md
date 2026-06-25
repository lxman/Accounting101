# Post-mortem: month-10 orphaned invoices (closed-period date + non-atomic issue)

**Date:** 2026-06-25
**Source:** full 24-month dog-food run (Accounting101.Simulation), surfaced at month 10 (Oct 2024).
**Severity:** Medium — six invoices orphaned (Issued, no journal entry); books understated until corrected.
No engine defect. The engine behaved correctly throughout.

> **This supersedes the earlier "concurrent-post sequence race" write-up, which was wrong.** That
> hypothesis was investigated and disproven (see "What it was not"). Red-first testing caught the
> misdiagnosis before any fix shipped.

## What happened

In month 10, all six A/R invoice `issue` calls returned HTTP 500 wrapping a ledger **409**; zero invoice
entries posted; reconcile flagged `humanError = 4` (A/R, consulting revenue, license revenue, sales tax
all short). The six invoices were left `Issued`, numbered, with no journal entry — orphans.

## Root cause (confirmed from the agent transcript)

The month-10 AR agent stamped its invoices with `issueDate = 2024-01-15` — January — almost certainly
copied from the AR brief's file-shape example (written for "month 1 = January 2024"). January had been
closed since month 1, so `LedgerService.EnsureOpenAsync` correctly refused the post (mapped to 409 at
`LedgerEndpoints.cs`), identically for all six. The cash receipts that month were dated `2024-10-31`
correctly and posted fine — which is why only the invoices failed. **The reconciler's `humanError`
attribution was right; the engine guarded itself.** This is a clerk data-entry error, not a system bug.

## What it was *not*

A "concurrent-post sequence race." When two transactional audit-chain appends collide on the unique
`(clientId, sequence)` index, Mongo raises a **WriteConflict** (a `TransientTransactionError`), and
`WithTransactionAsync` already retries it — the chain self-heals. A deterministic test
(`Post_retries_when_a_concurrent_append_commits_the_audit_tail_mid_transaction`, since reverted) was
written to reproduce the supposed fatal DuplicateKey and **passed without any fix**, proving the audit
chain is safe under concurrency. The journal counter is an atomic `findAndModify $inc` and cannot
collide. "All six failed identically" is systematic, not a race. There is no engine race to fix.

## The genuine product gaps (and status)

1. **Ergonomics — FIXED (commit `3b9015a`).** The receivables ledger client called
   `EnsureSuccessStatusCode`, throwing a bare `HttpRequestException` that discarded the engine's status
   and body, so "period is closed" surfaced as an opaque 500. `HttpLedgerClient` now throws a typed
   `LedgerClientException` (status + reason) and the issue endpoint relays the real 409/422 with the
   message — the clerk sees *why*.

2. **No proactive guard — SPECCED.** `IssueAsync` finalizes the invoice (assigning the number) before
   posting, so a rejected post orphans it. The fix is to validate the would-be post against the engine's
   own rules **before** finalizing — see `docs/superpowers/specs/2026-06-25-issue-preflight-validate-entry-design.md`.
   A predictable closed-period date is then caught while the invoice is still a `Draft`: the clerk fixes
   the date and re-issues, no orphan. The residual TOCTOU race (period closes between validate and post)
   is covered by a close-time exception backstop (separate spec).

3. **Harness footgun — TODO.** The AR brief's example uses a January date, which invited exactly this
   slip. Change the example to a neutral placeholder so the sim stops tempting it.

## Lessons

- Trust the reconciler's `engineBug` vs `humanError` attribution — it was correct; overruling it sent the
  investigation down a wrong path.
- Capture the response **body**, not just the status. The swallowed 409 hid "period is closed" from both
  the clerk and the post-mortem; gap #1 fixes that.
- Red-first testing earned its keep: the deterministic reproduction passing without a fix is what exposed
  the misdiagnosis before code shipped.
