# Multi-Firm Atlas Tenancy — Design

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)

## Problem

Today the platform models **one firm → many clients (sets of books)**. The
`Mongo:ControlDatabase` config binds a running host to exactly one control DB,
and `ControlStore`'s own doc comment states it plainly: *"One control DB per
deployment; there is no firm dimension because a deployment serves exactly one
firm."*

We need to deploy this two ways:

- **SaaS (web):** a **single running instance** hosting **many firms**. Each
  firm is an accounting business with **many clients** of its own.
- **On-site:** a single firm (the business itself) with many clients — i.e.
  today's model, unchanged in behavior.

So we must introduce a **firm tier above the control DB**, deploy it on a single
MongoDB Atlas cluster to start, and keep a clean path to spread firms across
multiple clusters later without touching the ledger engine.

## Scope

**In scope:**
- Three-tier database topology (platform → firm control DB → client ledger DB).
- Automatic tenant isolation via firm-scoped resolution.
- `clusterKey` on the firm (the firm as a self-contained, movable unit).
- Single→multi-cluster expansion mechanism + a concrete, measurable trigger.
- Three admin tiers (platform operator > firm admin > per-client roles).
- Firm provisioning (per-firm capability-set seeding).
- On-site mode as a one-firm collapse of the same code path.
- A **metering/entitlement surface** for billing: firm status, client status +
  timestamps, per-client enabled-modules — with module entitlement enforced at
  the existing authorization chokepoint.

**Out of scope (own later spec):** the payment subsystem — Stripe integration,
dunning, firm-facing invoices, proration math. This spec produces a clean
*meter*; a future billing subsystem *consumes* it.

**Out of scope (documented future modification):** a single firm large enough to
overflow one cluster (~5,000 clients). See "Whale firms" below — deliberately
deferred; no code built for it now.

## Chosen approach

**DB-per-client under a DB-per-firm control DB, with the firm as the unit of
cluster placement.** This mirrors the isolation pattern already in the codebase,
one tier up. A firm becomes a self-contained set of databases — a "movable
brick" — so cluster expansion is pure seam work and per-firm export/delete stays
a database-level operation.

### Alternatives considered

- **Shared collections with a `(firmId, clientId)` discriminator on every
  document.** Scales to effectively unlimited tenants on one cluster, but
  discards the per-tenant isolation the entire engine is built on: every store,
  query, and index is rewritten to carry the tenant key, blast-radius isolation
  weakens, and "export/delete one firm" stops being a `dropDatabase`. Wrong
  trade for sensitive accounting data at the target scale. **Rejected.**

- **A dedicated Atlas cluster per firm.** Ultimate isolation / data-residency
  and clean per-firm billing separation, but an Atlas cluster minimum per firm
  is expensive and ops-heavy. **Not the default** — but note it falls out of the
  chosen approach for free: a firm whose `clusterKey` points at its own
  dedicated cluster *is* cluster-per-firm, with zero code difference. So the
  chosen approach subsumes this as a per-firm option for a future regulated firm.

## Topology

All three tiers live on one Atlas cluster to start.

```
platform_control                     ← ONE per SaaS install (the new tier)
  ├─ firms         : firmId → { name, controlDb, clusterKey, status, timestamps }
  └─ clusters      : clusterKey → { connectionString }   (one row today: "default")

firm_{firmId}_control                ← ONE per firm (today's control DB, firm-scoped)
  ├─ clients       : clientId → { name, ledgerDb, status, enabledModules,
  │                               SoD, fiscalYearEnd, timestamps }
  ├─ memberships
  ├─ modules
  ├─ capabilitySets
  └─ adminAudit

firm_{firmId}_client_{clientId}      ← ONE per client (today's ledger DB, firm-prefixed)
  └─ journal, balances, checkpoints, audit, accounts, sequences
```

**Firm-prefixed naming is deliberate.** Every database belonging to firm X
shares the `firm_{firmId}_` prefix, so a firm's entire footprint is enumerable
with one name filter. That is exactly what makes a firm a migratable / deletable
unit (the "movable brick").

## Isolation model

Isolation is **structural, not a scattered set of checks.**

A request carries `firmId` from its token. The resolver reaches a client DB
*only* by going through that firm's own control DB:

```
firmId (claim) → platform_control.firms → firm_{firmId}_control
                                        → clients[clientId] → firm_{firmId}_client_{clientId}
```

A `clientId` belonging to firm B, presented with a firm-A token, is looked up in
firm A's `clients` registry, finds nothing, and is refused. **One firm can never
name another firm's data because it has no registry entry to resolve it
through.** This is the same "refuse the unregistered id" guarantee
`ClientDatabaseResolver` already gives today, inherited one tier up.

## Seams to introduce

The ledger engine, stores, and endpoints must remain unaware that firms or
clusters exist. Everything below is confined to resolution/composition:

1. **`PlatformStore`** (new) — persistence over `platform_control`
   (`firms`, `clusters`). One per SaaS install. On-site: holds exactly one firm.

2. **`IMongoClientFactory`** (new) — `Get(clusterKey) → IMongoClient`, one pooled
   client per cluster, resolved from `platform_control.clusters`. Today it has a
   single `"default"` entry; a second appears when cluster #2 is added.

3. **`IFirmControlResolver`** (new) — `firmId → ControlStore` scoped to that
   firm's control DB on that firm's cluster. Mirrors `IClientDatabaseResolver`
   one tier up. `ControlStore`/`AdminAuditStore` change lifetime from
   process-singleton to firm-resolved-per-request; their *code* is unchanged
   (they already take an `IMongoDatabase`).

4. **`IClientDatabaseResolver`** (existing seam, extended) — now resolves the
   firm's cluster + control DB first, then the client registration, then the
   client DB on the firm's cluster. This is the single place the doc comment
   already anticipated changing.

5. **Auth / claims** — add a `firmId` claim; `ClaimsActorFactory` maps it. The
   existing `admin=true` policy becomes **firm admin** (now firm-scoped). A new
   `platform=true` policy gates the platform surface.

6. **`CapabilitySetSeeder`** — changes from a boot-time `HostedService` seeding
   the single control DB to a **per-firm provisioning step** run when a firm is
   created. (On-site may still seed its single firm on startup.)

## Admin tiers

| Tier | Claim | Operates on | Can do |
|---|---|---|---|
| **Platform operator** | `platform=true` | `platform_control` only | Create/suspend firms, assign a firm's `clusterKey`, register clusters. Cannot see inside a firm's books. |
| **Firm admin** | `admin=true`, scoped by `firmId` | that firm's control DB | Create clients, manage memberships & capability sets, enable modules per client |
| **Per-client roles** | capabilities (unchanged) | one client's ledger | Post / approve / read per existing RBAC |

Today's `admin=true` becomes "firm admin": every existing admin endpoint
resolves its control DB from the `firmId` claim instead of the hard-coded
singleton. The new `platform=true` gates the new platform endpoints and can see
firm/cluster metadata but never a firm's ledger contents.

## Firm provisioning

New platform endpoint, `POST /platform/firms`:

1. Insert the firm into `platform_control.firms` with `status = Active` and a
   `clusterKey` (default `"default"`).
2. Create `firm_{id}_control` and run the capability-set seeder **against it**
   (the seeder that runs once on startup today becomes this per-firm step).
3. Create the firm's first firm-admin membership.

Suspending a firm flips `status = Suspended` (drives the basic fee and access
cut-off); it never deletes data.

## On-site mode

The **same code** with the firm dimension collapsed to one:

- `platform_control` holds exactly one firm; on-site auth injects a fixed
  `firmId` claim.
- Platform endpoints disabled by config; firm admin works exactly as today.
- **One code path, no fork** — on-site is "a SaaS install with one firm and no
  platform operator."

## Metering & entitlement surface

Three billing dimensions, each backed by a tenancy record we already keep or add
cheaply. Pricing/payment logic is out of scope; this section defines only the
data the meter reads.

| Dimension | Backed by | Also gates access? |
|---|---|---|
| Basic fee / firm | `platform_control.firms` (+ `status`) | — |
| Per-client / month | client `status = Active` + timestamps | — |
| Per-module / client | client `enabledModules` | **yes**, at existing chokepoint |

**Client lifecycle** — add `status` (`Active | Archived`) + created/archived
timestamps to `ClientRegistration`. This is load-bearing: accounting data must
be **retained for years**, so a firm closing a client flips status to `Archived`
(stops the meter) while the books stay intact. Billing counts `Active` only.
Without this, "stop billing a closed client" would force either deleting
regulated data or paying indefinitely.

**Firm lifecycle** — `status` (`Active | Suspended`) on the firm, as above.

**Per-client module entitlement** — add `enabledModules: string[]` to
`ClientRegistration` (e.g. `["receivables","payables","payroll"]`). Chosen at
per-client granularity (not per-firm) because it matches the per-client/month
billing grain and how practices actually work (one client needs Payroll, another
doesn't). A firm-level "allowed modules" upper bound is a nicety, **deferred**.

This field does **double duty**:
- **Meter:** bill = (active clients × basic) + Σ(enabled modules × module fee).
- **Access gate:** RBAC already enforces at a *single* chokepoint
  (`ModuleAccess.AuthorizeAsync`, ~40 endpoints). Adding "…and this client has
  the calling module enabled" is **one more check at that one place** — a client
  without Payroll enabled cannot reach Payroll endpoints. No new enforcement
  surface.

**Usage read** — a platform-level query returning, per firm as-of a date: active
client count and enabled-module counts. A future billing job snapshots this
monthly.

## Expansion: single cluster → many clusters

**Now (one cluster):** one `IMongoClient`, one `clusters` row (`"default"`),
every firm's `clusterKey = "default"`.

**Later (add cluster #2):**
1. Add a `clusters` row (`"cluster-2" → connectionString`).
2. `IMongoClientFactory` now pools a second client.
3. Point **new** firms' `clusterKey` at `"cluster-2"`; existing firms never move.
4. To *rebalance* an existing firm (rare): Atlas Live Migrate (or
   `mongodump`/`mongorestore`) its `firm_{id}_*` databases to the new cluster,
   then flip its `clusterKey`. Clean because a firm is a self-contained brick.

**The only code that changes at expansion is the two resolution seams**
(firm→cluster, cluster→client). The engine, stores, and endpoints never learn
that clusters exist.

### Concrete expansion trigger

Make "expand when we need it" measurable, not vibes. Watch
**collections+indexes per cluster.**

- Per client DB ≈ 6 collections × ~2 indexes ≈ **~15 storage objects**.
- Atlas/WiredTiger stays healthy to roughly **10k objects** on a standard tier
  (higher on larger tiers, but a known pain zone past that).
- Comfortable ceiling ≈ **~600–1,000 clients/cluster.**

**Alarm at ~8k objects per cluster (~80% of comfort).** At the alarm, stand up
cluster #2 and route new firms to it. By the time this matters we are past
free-tier Atlas anyway, so a second paid cluster is proportionate to revenue.

### Whale firms (deferred)

A **single** firm with ~5,000 clients (~75k objects) overflows any one cluster,
and "spread firms across clusters" doesn't help because it's one firm. If we
ever land one:

- **Spread that firm's clients across several clusters** — relax "firm = one
  clusterKey" to a per-client `clusterKey` override for that firm (~1,000/cluster
  × 5). Firm-level rollups fan out app-side across clusters (they already fan out
  app-side — Mongo has no cross-database `$lookup`). Engine untouched.
- **Or run that one firm on a shared-collection backend** — a targeted, per-firm
  storage strategy; keeps it on one cluster at the cost of a second store
  implementation.
- A dedicated/sharded cluster helps volume and noisy-neighbor isolation but does
  **not** by itself beat the collection-*count* ceiling (sharding distributes a
  collection's documents, not its catalog overhead).

This is deliberately **not built now.** A 5,000-client firm is major revenue and
real ops; we'd do targeted work then against the customer's actual requirements.
The isolation boundary is per-client-DB either way, and the normal multi-cluster
expansion already builds request-time `clusterKey` resolution — so the whale
override is a small, localized change to code we'll already have, not a rewrite.

## Testing strategy

- **Isolation:** a firm-A token presenting a firm-B `clientId` is refused
  (resolves against firm A's registry → not found → 404/403). The core
  guarantee; test at the resolver and end-to-end.
- **On-site collapse:** the single-firm path behaves identically to today's
  behavior (existing admin + ledger suites pass with the fixed `firmId` claim).
- **Provisioning:** `POST /platform/firms` creates the control DB, seeds
  capability sets, and creates the first firm admin; the new firm is immediately
  usable and empty.
- **Entitlement gate:** a client without a module enabled is refused at the
  module chokepoint; enabling the module admits it. Meter reflects the change.
- **Lifecycle:** archiving a client stops it counting as `Active` while its
  ledger DB remains intact and readable.
- **Cluster factory:** `IMongoClientFactory` returns a stable pooled client per
  `clusterKey`; unknown `clusterKey` fails closed.

## Open questions

- **Platform operator auth:** what issues the `platform=true` claim? Likely a
  separate, tightly-held credential distinct from firm IdP. (Detail for the auth
  slice; does not change the topology.)
- **Firm slug vs GUID in DB names:** `firm_{guid}_*` is collision-proof but
  opaque; a human-readable slug is friendlier for ops. GUID recommended for
  safety; revisit if ops ergonomics demand a slug map.
