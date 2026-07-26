# Module entitlement alignment — availability = installed ∩ entitled

**Date:** 2026-07-26
**Repo:** `Accounting101`

## Why

A paid module has two independent gates, and nothing in the system computes their intersection.

- **Installation** is per-*deployment*, decided at build time. `Modules.Private/` is gitignored and
  the Host globs `..\Modules.Private\**\*.Api.csproj` as ProjectReferences
  (`Accounting101.Host.csproj:30`). `ModuleCompositionDiscovery` then walks deps.json for libraries
  of type `project` — a filter chosen precisely because it "admits Modules.Private overlays and never
  NuGet packages." A module is installed iff its overlay was checked out when the Host was built.
- **Entitlement** is per-*client*, decided at runtime: `ClientRegistration.EnabledModules`, enforced
  default-closed at `ModuleAccess.cs:56-59`.

Because the two are never reconciled, a client can be entitled to a module this deployment does not
have. The route guard passes (it reads entitlement), the nav link renders, the operator uploads a
file, and the POST 404s against a route nobody registered. The guard says as much about its own
limits: *"This is UX, not security: the endpoint's own EnabledModules check is the authority"*
(`module.guard.ts`).

The trigger was the e-invoicing module. Its entire client-visible surface is one call —
`POST /clients/{id}/einvoicing/inbound/preview` — and that call *is* the parse, so without the
private DLL the import screen has nothing to render. It is the first module where installation and
entitlement can genuinely diverge.

`ModuleCompositionDiscoveryTests.Discovers_exactly_the_seven_module_compositions` fails on any
checkout that has the overlay present, for the same underlying reason: it pins an exact global set of
compositions, which cannot be right once composition is deployment-dependent. (Confirmed failing at
`8d2a255`, before any of the retry-key work. The overlay itself lives in a gitignored checkout, so
this repo's history cannot date when it first broke.)

## Deployment model this is designed against

**SaaS today, self-hosted on the roadmap.** That combination fixes the shape of the answer:

- Multi-tenant SaaS means one binary serves many clients with different entitlements, so
  **installation can never follow entitlement** — runtime entitlement must stay authoritative for
  authorization.
- Self-hosted means the customer owns the control database, so **`EnabledModules` is not a licensing
  control there** — they can flip it. Only the absence of the DLL from the build is a real gate.

**Out of scope, deliberately:** the packaging discipline that makes self-hosted licensing real —
which overlays a customer's build gets, how a release is verified to contain only what was sold, and
what stops an operator dropping a DLL in afterwards. That is release engineering, it is a separate
sub-project, and it should be designed when self-hosted is closer. This spec makes the application
code honest; it does not claim to make the DLL unobtainable.

## The rule

**Availability = installed ∩ entitled.** Every surface that answers "can this client use module X?"
answers with the intersection. Surfaces that answer a different question — what was *sold*, or what
is *installed* — keep answering that question, and say which one they mean.

## The seam

One new singleton, `InstalledModules`, exposing `IReadOnlySet<string> Keys`. Registered once:

```csharp
services.TryAddSingleton(sp => new InstalledModules(sp.GetServices<ModuleRegistration>().Select(m => m.Key)));
```

It derives from the same source as everything else — every module already calls
`AddModule(new ModuleIdentity("payables"), "Payables", …)`, which registers a
`ModuleRegistration { Key, Name, Enabled }` singleton (`ModuleHostingExtensions.cs:49-54`). So the
installed set cannot drift from what the Host actually composed.

The registration goes inside `AddModule` alongside the existing ones, which is subtler than it looks
and easy to get wrong: the **first** `AddModule` call registers the factory and every later one is a
`TryAdd` no-op, but the factory does not run until first resolution — at request time, long after
every `AddModule` call has completed. So the single `InstalledModules` instance sees *all* modules,
not just the first. Do not "fix" this by computing the key set eagerly at registration time; that
would capture only the modules registered so far.

**Taken from DI, not from the control database.** `ModuleRegistrar` does seed a copy of every
registration into the `modules` collection, but only into the *default firm's* control DB
(`ModuleRegistrar.cs:20-27`), so under multi-firm that copy can be partial or stale. DI is the
in-process truth, costs nothing, and needs no read.

**A dedicated type rather than injecting `IEnumerable<ModuleRegistration>` into read paths**, for a
reason beyond tidiness: `ModuleRegistration` carries `Secret`, the module's shared credential
(`ModuleRegistration.cs:22-28`, *"Never logged or surfaced to end users"*). Passing that object into a
capabilities handler puts a secret one careless serialization away from a response body.
`InstalledModules` carries keys and nothing else.

The type discovers nothing itself — discovery stays in `AddModule`, which already knows the key.
That keeps it a dumb value object with a public constructor, which is what makes the "not installed"
branch testable at all (see Testing).

## Every consumer of `EnabledModules`

| Consumer | Question it answers | Change |
|---|---|---|
| `ModuleAccess.cs:59` — authorization | may this call proceed? | **None.** If a module is not installed its code is not running, so no caller can present its credential; intersecting is a no-op. It remains the authority. |
| `CapabilitiesEndpoints.cs:37` — the UI's source of truth | what can this user reach? | **Intersect.** |
| `PostingAccountEndpoints.cs:33` — which config slots to offer | what can this client configure? | **Intersect.** Offering posting-account slots for an absent module is the same lie in a different place. |
| `AdminEndpoints.SetClientModules` | what may be sold to this client? | **Reject** keys this deployment does not have — 422 naming them. Defence in depth. |
| `AdminContracts.cs:41` — client detail | what was sold, and what is here? | **Keep entitlement raw, add installed.** This is where support distinguishes "not sold" from "not installed". |
| `PlatformEndpoints.cs:138` — usage meter | what was sold, across firms? | **Keep raw.** A billing meter counts what was sold, independent of whether any given deployment composed it. |

The last two rows are decisions, not oversights. Diagnosability belongs on the admin surface that
needs it, not in every user's capabilities payload; and metering entitlement rather than availability
is what makes the number a billing number.

## The Angular side changes nothing

`caps.moduleEnabled()` already reads the capabilities list. Its meaning shifts from "sold" to
"available", which is what the route guard always meant. No new wire field, no consumer edits, no
change to `RouteSentinelService`'s symmetric check.

**Consequence to accept:** under this design a misconfigured install (entitled, not installed) looks
identical to an unsold one from the UI — the feature is silently absent rather than visibly broken.
That is the right failure for an end user and the wrong one for support, which is exactly why the
admin client-detail surface keeps both fields.

## Entitlement is never erased

A client entitled to a module that is later uninstalled loses the feature at read time, but **the
stored `EnabledModules` entry stays**. Re-installing restores it. The intersection is a view, not a
migration: a redeploy must never silently delete what a customer bought.

This is also why `SetClientModules`' write-time guard is not sufficient on its own. Validating at
write time gives `entitled ⊆ installed` only until the next deployment changes what is installed;
nothing re-validates stored rows. The read-time intersection is what survives redeployment, and the
write-time guard is defence in depth on top of it.

## Testing

**The constraint that shapes the design.** The test process always has every module installed —
`ModuleCompositionDiscovery` walks its own deps.json, which is why it currently finds eight. A module
cannot be uninstalled in a test. So `InstalledModules` must be substitutable, and tests replace the
registration through `ConfigureTestServices`, which runs after the host's own and can `Replace` it.
`TryAddSingleton` is first-wins, so an override must go through that path rather than a second `Add`.

**The intersection**, on both read paths (capabilities and posting-account slots):

- entitled + installed → key present
- entitled + **not** installed → key absent
- not entitled + installed → key absent (already true; pin it — it is the default-closed property)

**`SetClientModules`:**

- an installed key → 200, persisted
- an absent key → 422 naming it
- **a request mixing one installed and one absent key rejects the whole request and persists
  nothing.** A half-applied write is worse than either clean outcome, and this is the assertion whose
  absence would let one ship.

**Entitlement survives uninstall:** read capabilities through an `InstalledModules` lacking a key the
client is entitled to, then read the raw client record and assert `EnabledModules` still contains it.

**Admin client detail** exposes both fields, and they differ in the constructed case.

**Discovery test:** `Discovers_exactly_the_seven_module_compositions` becomes a superset assertion —
the seven public compositions are all discovered, silent about extras. Its original purpose must
survive: the comment at `ModuleCompositionDiscoveryTests.cs:17-18` says it exists so *"a
pruned/undiscovered module fails here loudly."* The superset form still does that, and
`Discovery_order_is_deterministic` is untouched.

**Not tested, deliberately:** simulating a genuinely absent overlay end to end by manipulating
deps.json. Too fragile, and substituting `InstalledModules` covers the behaviour that matters.

**Expected blast radius on the existing suite: nil.** The test process has all modules installed, so
every intersection is a no-op there and seeded clients keep the entitlements they had. One thing to
verify during implementation rather than assume: whether any server-side test asserts `einvoicing`
appears in a capabilities payload. Such a test would fail on a public-only checkout and needs the
same substitution treatment.

## Global constraints

- .NET 10; Angular 22; build with 0 new warnings; TDD; commit per slice; stage explicit file lists.
- `UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev-config edit that must
  stay uncommitted.
- No new npm or NuGet dependency.
- No module gains a reference to another module. `InstalledModules` lives beside the existing module
  hosting types in the engine, which every module already references.
- Pre-existing and out of scope: 4 NU1902/NU1903 NuGet advisories from `Accounting101.TestSupport`
  transitive deps; 13 NG04002 console errors in the Angular suite.
