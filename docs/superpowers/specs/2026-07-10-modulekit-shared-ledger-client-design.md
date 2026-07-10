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
per-module (ISP preserved). This is a **pure, behavior-preserving refactor** — no
observable behavior change — that also gives the previously-uncovered resolver its
first direct tests and makes closing the deferred write-path relay gap a trivial
follow-up.

## Non-goals (explicit — not bundled here)

1. **Closing the write-path relay gap.** post/reverse/void still surface 500 (not a
   clean 4xx) where their endpoints don't `catch (LedgerClientException)`. This
   refactor makes the typed exception uniform across all modules, turning that
   closure into a one-catch-per-endpoint follow-up — but does not do it here (it is
   a deliberate behavior change).
2. **Unifying the `ILedgerClient` interfaces.** They stay narrow and per-module by
   design (ISP: consumers see only the methods they use). Only the *implementation*
   is shared.
3. **Onboarding/startup chart validation** — unrelated deferred item.
4. **Touching the engine.** Nothing in `Backend/` changes; ModuleKit references the
   engine, never the reverse.

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
  `Accounting101.Ledger.Contracts`.
- **`Accounting101.ModuleKit.Api`** (Api-side) — references
  `Accounting101.ModuleKit` + `Accounting101.Ledger.Api` +
  `Microsoft.AspNetCore.Http.Abstractions`.

Concrete proof the exception belongs in the domain-safe half: the AR/AP **domain**
services (`InvoiceService`, `BillService`) already `catch (LedgerClientException)`,
so those domain projects must see the type without an Api dependency.

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
  - All operations call `EnsureSuccessAsync` (relay everywhere) — see Behavior
    Preservation for why this is observable-behavior-neutral.
- **`AddModuleLedgerClient<TInterface, TClient>(this IServiceCollection, string name)`**
  — a DI extension standardizing the named typed-`HttpClient` + typed-client
  registration each module's `ServiceExtensions` does by hand today, where
  `TClient : ModuleLedgerClient, TInterface` and `name` is the module key /
  HttpClient name.

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
                 → module endpoint / domain service catch relays it (unchanged per module)

CashService/PayrollService status read → LedgerTruth.ShowsVoided(entriesForOneDoc)
   → union with envelope status (promote-only) — unchanged
```

## Behavior preservation (the acceptance bar)

Zero observable behavior change:

- **Relay per module is preserved.** The base throws `LedgerClientException` on any
  non-2xx. AR/AP/Reconciliation already relayed on every path → identical. FA/
  Inventory relayed on reads and 500'd on writes → still 500 on writes (their write
  endpoints don't catch it; the internal exception type changes, the client-observed
  result doesn't). Cash/Payroll had no relay → same, still 500 where uncaught.
  Nothing an endpoint currently catches changes; nothing it doesn't catch changes.
- **`LedgerTruth.ShowsVoided` is bit-identical** to the two resolvers it replaces.
- **Credential attach, `ViaModule` stamping, named clients** all preserved — the
  engine authorizes and stamps exactly as before.

## Migration & blast radius

**New (once):** the two ModuleKit projects + a `ModuleKit` test project, added to
`Accounting101.slnx`. Green with zero consumers first.

**All 7 modules:** `HttpLedgerClient.cs` → ~5-line 2b subclass; `.Api` `.csproj` +=
`ModuleKit.Api`; `ServiceExtensions` adopts `AddModuleLedgerClient<…>(…)`.

**5 modules with `LedgerClientException`** (AR, AP, Reconciliation, FA, Inventory):
delete the file; re-namespace every reference to
`Accounting101.ModuleKit.LedgerClientException` — catch sites in `.Api` endpoints
**and** the AR/AP domain services (`InvoiceService`/`BillService`); those domain
projects += `ModuleKit`.

**Cash + Payroll:** delete `CashLedgerStatus.cs` / `PayrollLedgerStatus.cs`;
`CashService`/`PayrollService` call `LedgerTruth.ShowsVoided(...)`; domain `.csproj`
+= `ModuleKit`. They also inherit the base's relay helpers (behavior-preserving).

**Tests:** module `Fakes.cs` implement each `ILedgerClient` directly (not via the
base) → untouched except any referencing `LedgerClientException` by its old
namespace.

**Ordering (green at every commit):** build both ModuleKit assemblies + unit tests
first → migrate **Cash first** as the pattern-setter (it exercises *both* assemblies
— resolver and client base) and verify → then the remaining six, deleting each
module's local copies only as it migrates.

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
  green unchanged; that is what proves behavior preservation. The relay E2E tests
  (`LedgerErrorRelayE2eTests`, AR/AP relay tests) exercise the base's relay through
  the real host post-migration.
- **No new E2E behavior** beyond the SDK's own unit tests.

## Success criteria

- One home each for `LedgerClientException`, `LedgerTruth.ShowsVoided`, the relay
  helpers, and the client plumbing; the 5/5/2/7 duplication counts drop to 1 each.
- Every module's concrete client is a ctor-only 2b subclass; narrow per-module
  `ILedgerClient` interfaces unchanged.
- Two new assemblies, correct dependency direction (arrows point toward the engine
  only); engine untouched.
- Whole solution green with no test changes beyond namespace updates + the new
  ModuleKit unit tests; behavior observably unchanged.

## Future Work (deferred — documented so the analysis isn't lost)

- **Write-path relay closure** — add `catch (LedgerClientException)` to the
  post/reverse/void endpoints (and the FA `UpdateAsset`/`ReactivateAsset` +
  Inventory `ReactivateItem` write-path re-fold endpoints) so they surface a clean
  4xx instead of 500. This refactor makes it uniform and trivial; it is a separate,
  deliberate behavior change.
- **`AddModuleLedgerClient` could grow** into a fuller module-registration helper
  (manifest, document store, authenticator wiring) if a future module author needs
  it — but YAGNI until then; this SDK covers only the ledger-client surface that is
  actually duplicated today.
