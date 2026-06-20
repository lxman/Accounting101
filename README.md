# Accounting101

A double-entry accounting engine where the journal is the single source of truth and everything else (balances, ledgers, the trial balance, the financial statements) is derived from it by replay. It's built so the books can't lie: an unbalanced entry can't be constructed, nothing posted is ever destroyed, and the subledgers can't drift from the general ledger because they're the same journal grouped two ways.

## Highlights

- **Illegal states can't be constructed.** The entry factory refuses to yield anything that doesn't balance, so an unbalanced entry has no representation that can reach the database.
- **Every number is derived, none is stored as gospel.** Balances come from folding the journal. The maintained projection is a cache that rebuilds from scratch and cross-checks against a replay, so drift is detectable and repairable, never silent.
- **The three statements articulate by construction.** Net income on the income statement is the equity line on the balance sheet; the cash flow statement's ending cash is the balance sheet's cash. The cash flow statement ties on the double-entry identity itself, so it stays balanced even when an account is classified wrong.
- **Tamper-evident by design.** The audit log is hash-chained and append-only. Replaying it reproduces the journal; replaying the journal reproduces the checkpoint. Divergence gets caught, not absorbed.
- **Concurrency-safe, and tested under fire.** Posts are contention-free inserts, projections update with atomic increments, lifecycle edits use optimistic concurrency, and the coordinator commits journal, projection, and audit in one replica-set transaction. Real concurrent-writer tests (racing approvals where exactly one wins, a 12-way audit-append race that still chains gaplessly) hold it to that.
- **O(1) balance reads.** Sub-millisecond at 50k entries against hundreds of milliseconds for a full replay, because reads hit the maintained projection and replay is bounded by period checkpoints.

## The idea

Accounting is journal replay. Entries are immutable facts; balances, the trial balance, and the financial statements are projections you get by folding the journal. Store the truth once at the highest fidelity and derive every lower-fidelity view from it. You get an audit trail, point-in-time reconstruction, and structural consistency for free, because there's only ever one set of facts to disagree with.

A correction is an event, not an edit. To fix an entry you supersede it: the original stays in the journal with every reference intact, and a replacement is appended. To undo one you void it, or reverse it into an open period. The history is the product.

## Architecture

Three tiers, each depending only on the one beneath it.

**Core** (`Accounting101.Ledger.Core`) is the domain, with zero external dependencies. The journal entry and its balancing invariant, the chart of accounts, replay, and the pure statement arrangers live here. No MongoDB, no HTTP, nothing. You can unit-test "assets equal liabilities plus equity" with no database in the loop.

**Mongo** (`Accounting101.Ledger.Mongo`) is persistence. One document per entry, append-only, money as Decimal128, GUIDs in binary. It carries the maintained balance projection (atomic `$inc`, so concurrent posts can't clobber each other), the hash-chained audit log, period checkpoints, and the coordinator that commits the journal, projection, and audit record together in a single replica-set transaction.

**API** (`Accounting101.Ledger.Api`) is the host: a thin ASP.NET Core minimal API where authentication, multi-tenancy, and policy live. The engine enforces only the invariants that must always hold. Everything else (who can do what, segregation of duties, step-up re-auth on dangerous actions) is the host's job, kept out of the core on purpose.

## What it does

Post compound, balanced journal entries under maker-checker approval. Correct them by superseding, reverse a posted entry into an open period, or void without a replacement. Close a period to snapshot and freeze it, and reopen it behind a re-auth step. Run the year-end close that resets the temporary accounts into retained earnings. Onboard a client by booking its carried-in trial balance as a single opening entry.

All three financial statements derive from the same journal and chart: balance sheet, income statement, and cash flow statement by the indirect method. Multi-tenancy is one client's books per database, with a per-deployment control database for the client registry and user roles. Five roles map to a permission matrix, segregation of duties is configurable per client, and identity sits behind a single provider-agnostic seam with a dev-token scheme today and room for JWT/OIDC.

## Design rules

Make illegal states unrepresentable. Keep the engine's enforced rules to the irreducible minimum (balance, period freeze, optimistic concurrency) and push policy upstream to the host. Recognize two orthogonal sources of truth: the journal is truth about what happened and how much, the chart of accounts is truth about what an account means. Classification (account type, normal side, cash-flow activity) lives on the chart and is never a stored value. Money is `decimal`, never float.

## Stack

.NET 10, C# 14, MongoDB. ASP.NET Core minimal API on the host. Tests are xUnit against EphemeralMongo, which stands up a real single-node replica set per run, so the suite exercises transactions and tenant isolation for real and needs nothing installed.

## Build and test

```
dotnet build Accounting101.slnx
dotnet test Accounting101.slnx
```

117 tests across the three layers. The Mongo and API suites pull a MongoDB binary on first run; everything else is self-contained.

## Status

The backend is done and proven: the engine, the host and its policy layer, and all three financial statements. A UI isn't built yet (the `UI/` folder is a placeholder). Next is the origination layer, the business documents (invoices, bills, payments) that emit journal entries instead of someone hand-keying debits and credits.

## License

MIT. See [LICENSE](LICENSE).
