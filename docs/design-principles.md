# Accounting101 — Design Principles

This is the canonical statement of what the product *is* and the rules it holds to. Every spec, module,
and front end defers to this document. If something here conflicts with code, the code is wrong or this
document is out of date — reconcile, don't ignore.

---

## 1. The thesis: the journal is the only truth

Accounting is journal replay. Entries are immutable facts; balances, the trial balance, and the three
financial statements are projections you get by folding the journal. Store the truth once at the highest
fidelity and derive every lower-fidelity view from it. You get an audit trail, point-in-time
reconstruction, and structural consistency for free, because there is only ever one set of facts to
disagree with.

Nothing derived is ever stored as gospel. The maintained balance projection is a cache: it rebuilds from
scratch and cross-checks against a replay, so drift is detectable and repairable, never silent. A number
is either an immutable journal fact or a function of those facts — never a third thing someone edited.

## 2. Corrections are events, not edits

To fix an entry you supersede it: the original stays in the journal with every reference intact, and a
replacement is appended. To undo one you void it, or reverse it into an open period. The history is the
product. There is no destructive update anywhere in the system — nothing posted is ever overwritten or
deleted.

## 3. Make illegal states unrepresentable

The entry factory refuses to yield anything that doesn't balance, so an unbalanced entry has no
representation that can reach the database. This is the general rule, not a one-off: prefer a design where
the bad state *cannot be constructed* over one that validates against it after the fact. Money is
`decimal`, never floating point — a float price is an illegal state we don't allow to exist.

## 4. The engine enforces the irreducible minimum; policy lives upstream

The engine enforces only the invariants that must *always* hold, for every client, forever: entries
balance, closed periods are frozen, and concurrent lifecycle edits are resolved by optimistic
concurrency. That is the whole list.

Everything else is policy and lives upstream in the host: authentication, authorization, the role
permission matrix, segregation of duties, step-up re-auth on dangerous actions. Policy varies by
deployment and customer; invariants do not. We do not hard-wire into the database any rule that isn't
absolutely irreducible. When in doubt, a rule is policy, and policy goes up.

## 5. Two orthogonal sources of truth

There are exactly two, and conflating them is a category error:

- The **journal** is truth about *what happened and how much* — immutable, append-only, the numbers.
- The **chart of accounts** is truth about *what an account means* — its type, normal side, cash-flow
  activity, retained-earnings flag.

Classification lives on the chart and is **never a stored value on a journal line**. Only
journal-derived numbers are values. An account being an asset is not a fact about any transaction; it's a
fact about the account. Keep them separate and the statements articulate by construction.

## 6. The statements articulate by construction

Net income on the income statement *is* the equity movement on the balance sheet. The cash-flow
statement's ending cash *is* the balance sheet's cash. The cash-flow statement ties on the double-entry
identity itself, so it stays balanced even when an account is classified wrong. We never compute the same
figure two ways and hope they agree; we derive each once from the shared journal and chart so they cannot
disagree.

## 7. Tamper-evidence is structural

The audit log is hash-chained and append-only. Replaying it reproduces the journal; replaying the journal
reproduces the checkpoint. Divergence gets caught, not absorbed. Integrity is a property of the data
structure, not a process someone has to remember to run.

---

## 8. The product is a module platform

Accounting101 is a **modular monolith**: separate projects in one solution, modules sold separately and
wired into one deployable host. The shape is deliberate and load-bearing:

- **The engine is the only thing that touches MongoDB.** No module opens a database connection. This is
  the security and integrity boundary — one secured store per client, one writer of record.
- **Modules are HTTP-only clients of the engine.** A module turns a business document (an invoice, a
  bill, a payment) into balanced journal entries by posting them over HTTP, forwarding the caller's
  token. It depends only on the wire **Contracts**, never on the engine's internals.
- **Modules persist their own documents through a generic, engine-hosted document store** with audit
  tiers (reference / evidentiary), not through private collections. The engine owns all data at rest.
- **A module makes no policy decisions.** It posts entries and leaves approval to the client's normal
  maker-checker flow, which already respects segregation of duties. A module is a pure engine client.

Tenancy: one client's books per database (the isolation unit), with a per-deployment **control database**
for the client registry, user roles, and the module registry. A firm is a *deployment* boundary, not an
in-app dimension — multi-firm is multi-deployment, against one canonical always-online store.

Layering, each tier depending only on the one beneath it:

- **Core** — the domain, zero external dependencies. The balancing invariant, the chart, replay, the pure
  statement arrangers. You can unit-test "assets equal liabilities plus equity" with no database in the loop.
- **Contracts** — the wire DTOs, zero dependencies. The boundary modules depend on.
- **Mongo** — persistence. One document per entry, append-only, money as Decimal128, the maintained
  projection, the hash-chained audit log, checkpoints, the replica-set-transaction coordinator.
- **Api** — a *library* of endpoints and services (`AddLedgerEngine` / `MapLedgerEndpoints`).
- **Host** — the composition root, the deployable. Authentication, multi-tenancy, and policy live here.

---

## 9. The contract is UI-agnostic — any front end must be able to ship against it

A core product principle: you could put an Angular, Vue, React, plain-HTML, WPF, WinForms, Qt, X11, or CLI
front end on this system and ship it. The front end is a swappable consumer; the value is the contract.
This is enforceable, not aspirational, if we hold one line:

> **The server owns numbers and domain semantics. The client owns formatting, layout, and interaction.**

Everything follows from that line.

- **Every value a UI would display must come from the API already computed.** Net income, running
  balances, A/R and A/P aging buckets, trial-balance subtotals, statement totals, period open/closed,
  the drill-link from an entry to its source document — all of it is *domain* logic and is computed
  server-side. The moment a client has to derive "is this invoice overdue," that logic exists in every
  front end and they drift. Clients render; they do not derive.
- **The contract carries data and semantics, never presentation.** Money is `decimal` plus an ISO
  currency code, never a preformatted string like `"$1,234.56"`. Dates are ISO-8601, not
  `"March 31, 2024"`. No HTML or markup in response bodies. No colors, display ordering, or layout hints.
  Account classification ships as data so any client can render debits and credits however it likes.
  Thousands separators, "show overdue in red," grid-vs-`printf` — all client-side, none of the server's
  business.
- **Transport is REST + JSON** — the genuine lowest common denominator that a browser, a WPF app, a Qt
  app, and a bash script can all speak. The API is described by **OpenAPI** so any stack can generate a
  typed client. Not gRPC-only or binary-only.
- **Errors are structured data** (ProblemDetails), never HTML error pages — every client parses them the
  same way.
- **Auth is a bearer token in a header**, not cookies or server-side sessions, which would couple the
  system to a browser. The engine is stateless request/response; there is no view-state.
- **Real-time is additive, never required.** SSE or WebSocket for the web if we want it, but the core is
  request/response so a CLI that polls is a first-class citizen.
- **Reserve currency on every monetary value now**, even while single-currency. Formatting is the
  client's job, but the currency a figure is *denominated in* is domain data — and it's the one seam
  that's expensive to retrofit and cheap to leave open.

The payoff compounds with the architecture: because clients hold no logic, a front end cannot be "upended"
by module development. New modules add rows to journal-derived statements and a new source-document
renderer keyed by `SourceType`; the read contract — which sits on the stable engine — does not move.

---

## 10. How the system is built

These are practices, not product invariants, but they are how the principles above stay true over time:

- **Build incrementally, commit per slice.** Each logical slice (a doc fix, a feature, a bug fix) is its
  own reviewed commit. Don't batch.
- **Test-first.** New behavior is driven by a failing test before the implementation exists. Tests run
  against EphemeralMongo — a real single-node replica set per run — so the suite exercises transactions
  and tenant isolation for real and needs nothing installed.
- **The dog-food simulation is the regression suite.** A standalone harness runs a realistic business
  (Summit Consulting) through the live stack under segregation of duties and reconciles the engine's
  actual books against the intended ledger. When it finds a bug, fix it in the library and re-run.
- **.NET 10 / C# 14 / MongoDB.** Latest stable dependencies; namespaces follow folder structure.

---

## Concurrency, in one paragraph

The posting path is contention-free: a post is an insert of a new GUID-keyed document, with nothing shared
to lock. Balances update by atomic `$inc`, which is commutative and lossless, so concurrent approvals
converge to the journal replay. Lifecycle edits use a version-conditional replace (surfaced as ETag /
If-Match → 412 on conflict). The audit chain is guarded by a unique `(clientId, sequence)` index, so a
concurrent fork becomes a duplicate-key failure that retries. The coordinator commits journal, projection,
and audit in one replica-set transaction; even without it, the journal is the source of truth and the
projection is a recomputable cache, so a crash drifts the cache (detectable, repairable), never the truth.
All intentionally-linear state is per-client, so contention shards by client and never by the firm.
