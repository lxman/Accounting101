# ModuleKit — Shared Module→Ledger Client & Helpers — Design

**Date:** 2026-07-10
**Status:** Approved (design)

## Problem

Every feature module talks to the ledger engine the same way — a typed
`HttpClient` that forwards the caller's bearer token, attaches the module
credential on writes, and shapes the engine's responses — and several of them now
carry byte-for-byte-identical support code alongside it. The duplication is
concrete and has crossed the rule-of-three:

| Construct | Copies | Character |
|---|---|---|
| `LedgerClientException` (class) | **5** — AR, AP, Reconciliation, FA, Inventory | identical, pure (extends `Exception`) |
| `EnsureSuccessAsync` + `ReasonFrom` (private helpers on `HttpLedgerClient`) | **5** — same modules | identical, pure over `HttpResponseMessage` + JSON |
| `ShowsVoided` ledger-truth resolver (`CashLedgerStatus` / `PayrollLedgerStatus`) | **2** — Cash, Payroll | identical, pure over `EntryResponse` |
| `HttpLedgerClient` plumbing (`Forwarded`, credential attach, `Post`/`Reverse`/`Void`, reads) | **7** — every module | near-identical; the per-module `ILedgerClient` interfaces differ by design (ISP) |

The `HttpLedgerClient` bodies are ~100 lines each and repeat the same bearer
forwarding, credential attach, and JSON handling seven times. The just-shipped
fold-on-read hardening added the third construct (`LedgerClientException` + the two
helpers) to FA and Inventory, taking those copies from 2 to 5 and making the
duplication impossible to keep ignoring. There is no shared module-support layer
today — each module is a self-contained "brick," so de-duplicating means creating
the first shared layer, deliberately.

## Goal

Extract the shared module→ledger surface into a small **ModuleKit** — a dev-SDK for
module authors — so the exception, the relay helpers, the ledger-truth resolver,
and the HTTP client plumbing each have exactly one home. Each module's concrete
client collapses to a thin subclass; its narrow `ILedgerClient` interface stays
per-module (ISP preserved). The extraction is behavior-preserving; on top of it,
because every module now throws the one typed exception uniformly, a **single
relay middleware** (provided by ModuleKit, registered once in the host) can
translate any escaping `LedgerClientException` into a clean 4xx — closing the
write-path relay gap for all modules at once and letting the ~22 scattered
per-endpoint `catch` arms be deleted. The resolver also gains its first direct
tests.

### Where the 500s come from (and why the fix is one middleware, not the engine)

The ledger engine is already correct: it returns clean 4xx `ProblemDetails` for
every refusal it owns (409 closed-period, 422 unbalanced / fold-dimension-mismatch,
403 SoD). A 500 is *manufactured inside the module* when the typed
`LedgerClientException` escapes an endpoint that doesn't `catch` it — ASP.NET's
default pipeline turns any unhandled exception into a 500. So there is nothing to
fix at the ledger level; the fix belongs one level *above* the endpoints, at the
module-host boundary. Once the refactor makes every module throw the single
`ModuleKit.LedgerClientException`, one middleware at that boundary relays it
faithfully — a real engine 500 stays 500 (honest), a clean 4xx is relayed as a 4xx
(no longer swallowed). This is the reusable payoff that justifies ModuleKit: the
relay becomes *one home*, not N endpoint catches.

## Non-goals (explicit — not bundled here)

1. **Unifying the `ILedgerClient` interfaces.** They stay narrow and per-module by
   design (ISP: consumers see only the methods they use). Only the *implementation*
   is shared.
2. **Onboarding/startup chart validation** — unrelated deferred item.
3. **Touching the engine.** Nothing in `Backend/` changes; ModuleKit references the
   engine, never the reverse.
4. **Retiring the host's existing exception middleware.** The host already maps
   `ModuleAccessDeniedException` → 403 and `JsonException` → 400 (`Program.cs:46`).
   That stays; the ModuleKit relay is an additional, disjoint middleware for
   `LedgerClientException` only.

## Scope decisions (and the reasoning)

**Two assemblies, split by layer — driven by a real constraint, not gold-plating.**
The `ShowsVoided` resolver is consumed in the **domain** layer (`CashService` /
`PayrollService` are domain classes; the Cash/Payroll domain projects reference
only `Ledger.Contracts`). The `HttpLedgerClient` base is consumed in the **Api**
layer and needs `Ledger.Api` (for `ModuleCredential`, which lives at
`Backend/Accounting101.Ledger.Api/Auth/ModuleCredential.cs`) and
`IHttpContextAccessor`. Putting everything in one Api-side assembly would force the
Cash/Payroll *domain* projects to depend on an Api assembly (dragging AspNetCore +
`Ledger.Api` down into the domain layer) just to share 8 lines — a layering
inversion. So:

- **`Accounting101.ModuleKit`** (domain-safe) — references only
  `Accounting101.Ledger.Contracts`. Holds `LedgerTruth` (used by Cash/Payroll
  *domain* services) and the pure `LedgerClientException`.
- **`Accounting101.ModuleKit.Api`** (Api-side) — references
  `Accounting101.ModuleKit` + `Accounting101.Ledger.Api`, with a
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (it needs
  `IHttpContextAccessor` for the client base and `IApplicationBuilder`/`Results` for
  the relay middleware). Holds the `ModuleLedgerClient` base, the relay middleware,
  and the DI extension.

The **forcing constraint** for the domain-safe assembly is the `LedgerTruth`
resolver: it is consumed in the domain layer (`CashService`/`PayrollService`), whose
projects reference only `Ledger.Contracts` and must not gain an Api dependency.
`LedgerClientException` is placed in the same domain-safe assembly because it is a
pure, dependency-free type and that is the Core tier the Api tier builds on — not
because domain code catches it (it does not: the AR/AP domain services only let it
propagate, so the doc stays Draft; only `.Api` endpoints and the relay middleware
reference the type). Consequently only **Cash and Payroll** domain projects gain a
`ModuleKit` reference (for `LedgerTruth`); the other modules touch ModuleKit only
from their `.Api` projects.

**`ILedgerClient` interfaces stay narrow and per-module.** The audit
([[accounting101-module-shared-sdk-audit]]) established these diverge by design:
AR/AP have `Approve`+`Validate`+`GetSubledger`; Cash/Payroll/Inventory have
`GetEntriesBySourceRefs`; FA/Inventory/AR/AP have `GetSubledger`; the minimal set
is Post/Reverse/Void/`GetEntriesBySourceRef`. The base implements the **union** of
these operations once; each interface selects its subset.

**Named concrete clients stay per-module.** The `FixedAssetsLedgerClient`-style
named-HttpClient convention (avoids `ILedgerClient` short-name collision across
modules; binds the module's keyed `ModuleCredential`) is unchanged — only the
implementation moves to the base.

## Architecture

### `Accounting101.ModuleKit` (domain-safe)

- **`LedgerClientException(int statusCode, string reason)`** — the typed relay
  exception, verbatim from the current copies, namespace `Accounting101.ModuleKit`.
  `int StatusCode`, `string Reason`.
- **`LedgerTruth`** — a static class with
  `bool ShowsVoided(IReadOnlyList<EntryResponse> entriesForOneDoc)`, the single home
  of the envelope-vs-ledger void rule. Body is the existing `CashLedgerStatus` /
  `PayrollLedgerStatus` implementation verbatim (they are already byte-for-byte
  equal): no non-reversal primary → `false` (fall back to envelope); any primary
  `Status == "Voided"` → `true` (withdrawn while pending); a reversal whose
  `ReversalOf` points at a primary → `true` (reversed); else `false`.

### `Accounting101.ModuleKit.Api` (Api-side)

- **`abstract class ModuleLedgerClient(HttpClient http, IHttpContextAccessor context, ModuleCredential credential)`**
  — carries all shared plumbing exactly once:
  - `protected HttpRequestMessage Forwarded(HttpMethod, string uri)` — attaches the
    caller's `Authorization` header (verbatim from today).
  - `EnsureSuccessAsync` + `ReasonFrom` — the relay helpers (verbatim), throwing
    `Accounting101.ModuleKit.LedgerClientException`.
  - The shared operations as **`public`** methods (sub-choice 2b, below), each with
    the exact signature the module interfaces already use:
    `PostAsync`, `ApproveAsync`, `ReverseAsync`, `VoidAsync`, `ValidateAsync`,
    `GetEntriesBySourceRefAsync`, `GetEntriesBySourceRefsAsync`, `GetSubledgerAsync`.
    The credential-attaching methods are `PostAsync`, `ReverseAsync`, `VoidAsync`,
    and `ValidateAsync` (they send `X-Module-Key`/`X-Module-Secret`); `ApproveAsync`
    and all reads do **not** attach it — the exact same rule as the current AR/AP
    implementations.
  - All operations call `EnsureSuccessAsync` (relay everywhere), so every non-2xx —
    read or write — throws `LedgerClientException` uniformly. This is the precondition
    the single relay middleware needs; see "Behavior: what's preserved, what improves."
- **`AddModuleLedgerClient<TInterface, TClient>(this IServiceCollection, string name)`**
  — a DI extension standardizing the named typed-`HttpClient` + typed-client
  registration each module's `ServiceExtensions` does by hand today, where
  `TClient : ModuleLedgerClient, TInterface` and `name` is the module key /
  HttpClient name.
- **`UseModuleLedgerExceptionRelay(this IApplicationBuilder)`** — a middleware that
  wraps the pipeline; on an escaping `LedgerClientException` it writes an
  `application/problem+json` response with `Status = ex.StatusCode` and
  `Detail = ex.Reason` (matching the host's existing hand-rolled exception
  middleware convention, `Program.cs:46`). It catches *only* `LedgerClientException`;
  every other exception falls through unchanged. Registered once in the host
  (`app.UseModuleLedgerExceptionRelay();`), it is the single home of the relay — a
  real engine 500 relays as a structured 500, a clean 4xx relays as that 4xx.

### Per-module concrete client (sub-choice **2b**: public base + selector interface)

Each module's `HttpLedgerClient.cs` collapses from ~100 lines to a ctor-only
subclass; inherited public base methods satisfy the narrow interface:

```csharp
public sealed class FixedAssetsHttpLedgerClient(
    HttpClient http, IHttpContextAccessor context,
    [FromKeyedServices("fixedassets")] ModuleCredential credential)
    : ModuleLedgerClient(http, context, credential), IFixedAssetsLedgerClient;
```

Consumers inject the narrow interface, so ISP holds at the seam that matters — the
*injected contract* stays narrow. The only give is that the concrete *class*
exposes the full operation union publicly (e.g. Cash's client technically inherits
`GetSubledgerAsync`); this is invisible because nothing injects the concrete class —
the named-client + DI convention always binds interface→class. (Sub-choice 2a —
`protected` cores + per-module delegation for a strictly-narrow class surface — was
considered and rejected: it reintroduces the per-module boilerplate the SDK exists
to remove, for a purity property no consumer observes.)

## Data flow

```
consumer → injects narrow IXLedgerClient
         → concrete XHttpLedgerClient (ctor-only) : ModuleLedgerClient
              → Forwarded(bearer) + [X-Module-Key/Secret on writes] → engine
              → EnsureSuccessAsync → (non-2xx) throw ModuleKit.LedgerClientException
                 → propagates out of the endpoint
                 → UseModuleLedgerExceptionRelay middleware → problem+json (ex.StatusCode, ex.Reason)

CashService/PayrollService status read → LedgerTruth.ShowsVoided(entriesForOneDoc)
   → union with envelope status (promote-only) — unchanged
```

## Behavior: what's preserved, what improves

**Preserved (the refactor half):**

- **`LedgerTruth.ShowsVoided` is bit-identical** to the two resolvers it replaces —
  Cash/Payroll status reads unchanged.
- **Credential attach, `ViaModule` stamping, named clients** all preserved — the
  engine authorizes and stamps exactly as before.
- **Paths that already relayed** (AR/AP/Reconciliation everywhere; FA/Inventory
  reads) return the **same** response: previously via a per-endpoint
  `Results.Problem(ex.Reason, statusCode: ex.StatusCode)`, now via the middleware,
  which produces the identical status + detail. Deleting those catch arms is
  behavior-identical.

**Improves (the relay-closure half — the intentional change):**

- **Uncaught write paths stop manufacturing 500s.** post/reverse/void on FA,
  Inventory, Cash, Payroll, plus FA `UpdateAsset`/`ReactivateAsset` and Inventory
  `ReactivateItem`, previously let the exception escape → 500. The middleware now
  relays the engine's real status (409/422/403) as `problem+json`. This closes the
  deferred write-path relay gap.
- **Genuine engine 500s remain 500** — the middleware relays whatever status the
  engine returned, so it never masks a real server fault; it only stops turning a
  clean 4xx into a 500.
- **Non-`LedgerClientException` exceptions** (module bugs) are untouched — the
  middleware ignores them; the default pipeline still 500s.

## Migration & blast radius

**New (once):** the two ModuleKit projects + a `ModuleKit` test project, added to
`Accounting101.slnx`. Register `app.UseModuleLedgerExceptionRelay();` in
`Accounting101.Host/Program.cs` (adjacent to the existing exception middleware,
before `MapXEndpoints`). Green with zero module consumers — the middleware catches
`ModuleKit.LedgerClientException`, which nothing throws yet, so it is inert until
modules migrate.

**All 7 modules:** `HttpLedgerClient.cs` → ~5-line 2b subclass; `.Api` `.csproj` +=
`ModuleKit.Api`; `ServiceExtensions` adopts `AddModuleLedgerClient<…>(…)`.

**5 modules with `LedgerClientException`** (AR 11, AP 5, Reconciliation 2, FA 2,
Inventory 2 — 22 `catch (LedgerClientException)` arms total): delete the local
`LedgerClientException.cs`; **delete every `catch (LedgerClientException)` arm**
(keep the sibling `catch (InvalidOperationException)` arms) — the global middleware
now owns that relay. Because each module only starts throwing
`ModuleKit.LedgerClientException` once its client is swapped to the base, the client
swap and the catch deletions happen in the **same task**, so at every commit the
module's relay is served by exactly one mechanism (its own catches before, the
middleware after). Their domain projects do **not** gain a `ModuleKit` reference
(they don't reference the type in code).

**Cash + Payroll:** delete `CashLedgerStatus.cs` / `PayrollLedgerStatus.cs`;
`CashService`/`PayrollService` call `LedgerTruth.ShowsVoided(...)`; domain `.csproj`
+= `ModuleKit`. They have no `LedgerClientException` catches today; after the client
swap they gain relay via the middleware for the first time (write-path improvement).

**Tests:** module `Fakes.cs` implement each `ILedgerClient` directly (not via the
base) → untouched except any referencing `LedgerClientException` by its old
namespace. FA/Inventory `LedgerErrorRelayE2eTests` keep passing — they now exercise
the middleware relay instead of a per-endpoint catch (still valid regression
guards).

**Ordering (green at every commit):** (1) build both ModuleKit assemblies + unit
tests **and register the middleware in the host** first — the relay is live before
any catch is deleted, so no read path regresses to 500. (2) migrate **Cash** as the
pattern-setter (exercises *both* assemblies — resolver and client base) and verify.
(3) migrate the remaining six, each deleting its local copies + catch arms in the
same commit.

## Testing

- **ModuleKit unit tests (new):** `LedgerTruth.ShowsVoided` truth table (no-primary
  → false; withdrawn-pending `Status=="Voided"` → true; reversal-exists → true;
  clean → false) — the direct coverage the resolver never had. Plus `ReasonFrom`
  priority (ValidationProblemDetails `errors` → ProblemDetails `detail` → raw body →
  status phrase) and `EnsureSuccessAsync` (2xx passes; non-2xx throws
  `LedgerClientException` with parsed status + reason), ported from the existing
  AR/AP `HttpLedgerClientTests`.
- **Existing per-module suites are the regression oracle** — FA 112, Inventory 94,
  and the AR/AP/Cash/Payroll/Reconciliation suites plus the whole-solution run stay
  green unchanged; that is what proves the refactor half preserved behavior. The
  relay E2E tests (`LedgerErrorRelayE2eTests`, AR/AP relay tests) exercise the
  middleware relay through the real host post-migration.
- **One new write-path relay E2E test** proving the closure: a module *write*
  (e.g. an FA depreciation-run void, or a Cash disbursement void, dated into a
  closed period) that the engine refuses now returns the relayed 4xx `problem+json`,
  **not** a 500 — the behavior this effort adds. Mirror the existing AR/AP
  `LedgerErrorRelayE2eTests` closed-period scenario, on a module whose write path
  previously 500'd.

## Success criteria

- One home each for `LedgerClientException`, `LedgerTruth.ShowsVoided`, the relay
  helpers, and the client plumbing; the 5/5/2/7 duplication counts drop to 1 each,
  and the 22 per-endpoint `catch (LedgerClientException)` arms drop to one host
  middleware.
- Every module's concrete client is a ctor-only 2b subclass; narrow per-module
  `ILedgerClient` interfaces unchanged.
- Two new assemblies, correct dependency direction (arrows point toward the engine
  only); engine untouched.
- **No module write path manufactures a 500 from a clean engine 4xx** — every
  ledger refusal (read or write) relays as `problem+json` with the engine's status
  and reason; genuine engine 500s still relay as 500.
- Whole solution green — existing suites unchanged except namespace updates, plus
  the new ModuleKit unit tests and the one write-path relay E2E.

## Future Work (deferred — documented so the analysis isn't lost)

- **`AddModuleLedgerClient` could grow** into a fuller module-registration helper
  (manifest, document store, authenticator wiring) if a future module author needs
  it — but YAGNI until then; this SDK covers only the ledger-client surface that is
  actually duplicated today.
- **Onboarding/startup chart validation** — prevent the fold-dimension misconfig up
  front rather than relay its 422 at runtime. Unrelated cross-module item; unchanged
  by this work.
