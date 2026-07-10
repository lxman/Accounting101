# Fold-on-Read Error Relay (FixedAssets + Inventory) — Design

**Date:** 2026-07-10
**Status:** Approved (design)

## Problem

The Fixed Assets and Inventory modules derive a stored-looking field from the
ledger on every read: FA's `AccumulatedDepreciation` is a `{Asset}`-dimensioned
subledger fold, Inventory's `TotalValue` is an `{Item}`-dimensioned subledger fold
(the ledger-first redesign, epic complete `e71121f`). Both folds run
**unconditionally** on the list and detail read paths, via
`ILedgerClient.GetSubledgerAsync` against a control account:

- FA: `FixedAssetsService.GetAsync` / `GetByClientPagedAsync` fold
  `AccumulatedDepreciationAccountId` with dimension `"Asset"`
  (`FixedAssetsService.cs:55,62`).
- Inventory: `ItemValuationService` folds `InventoryAssetAccountId` with dimension
  `"Item"` (`ItemValuationService.cs:25,46`), reached from
  `InventoryService.GetAsync` / `GetPagedAsync`.

The engine's `GET /clients/{id}/subledger` validates that the named account is a
control account requiring exactly that dimension. If the account is configured
**without the required dimension** — `RequiredDimensions` empty, or present but
not containing the fold's dimension — the engine returns **422 Unprocessable**
(`LedgerEndpoints.cs:554,557`). It can also 404 (account not found / wrong client)
or 409 (never on a plain read, but the same transport applies).

Both modules' `HttpLedgerClient` read methods call the bare
`response.EnsureSuccessStatusCode()` (FA `HttpLedgerClient.cs:82`, Inventory
`HttpLedgerClient.cs:93`). That throws an untyped `HttpRequestException`, which
escapes the read endpoint as an **opaque 500** — the `/assets` crash observed in
the live Inventory smoke, and the same latent crash on every FA and Inventory list
and detail screen whenever the folded control account is misconfigured.

Receivables and Payables already solved this exact transport problem: their
`HttpLedgerClient` throws a typed `LedgerClientException(int StatusCode, string
Reason)` via `EnsureSuccessAsync` + `ReasonFrom`, and their endpoints catch it and
relay `Results.Problem(ex.Reason, statusCode: ex.StatusCode)`. FA and Inventory
never got that mechanism — `LedgerClientException.cs` exists in
Receivables/Payables/Reconciliation but **not** in FixedAssets/Inventory.

## Goal

Make FA and Inventory read paths **honest**: when the engine refuses a fold read,
relay its real status and reason (a clean 4xx) instead of letting it escape as a
500. Match the AR/AP precedent exactly — new typed exception + `EnsureSuccessAsync`
on the read methods + a relay catch on the fold-reading endpoints. Nothing more.

## Principle (why this is relay-only, not a runtime guard)

This is a **transport-layer error-handling** change. It does not watch for,
tolerate, or correct bad data. The 422 the engine returns is *correct* — a folded
control account that lacks its required dimension is a genuine chart
misconfiguration, and the right response is to surface that fault to the caller,
not to degrade the fold to 0 or paper over it. Bad data only arises when *we*
change a shape; when it does, it is fixed with a small one-off patch, not a
standing runtime corrector. So: **clean 4xx, never degrade-to-0.** This mirrors the
AR/AP relay, which likewise surfaces the engine's refusal rather than masking it.

## Scope decisions (and the reasoning behind them)

**Read methods only.** The swap from `EnsureSuccessStatusCode()` to
`EnsureSuccessAsync(...)` is applied only to the fold-reading GETs:
`GetSubledgerAsync`, `GetEntriesBySourceRefAsync`, and (Inventory)
`GetEntriesBySourceRefsAsync`. These are the methods on the read path that can
surface a fold 422/404 to a list/detail screen.

**Post path deliberately untouched.** `PostAsync`, `ReverseAsync`, and `VoidAsync`
also call `EnsureSuccessStatusCode()` and would likewise 500 on an engine
rejection. That is a **separate, pre-existing latent item** — a write-path relay
gap, not the fold-on-read crash this slice targets. It is out of scope here;
touching it would widen the change, add write-path relay catches, and invite
scope creep into territory AR/AP handle differently (module-credentialed mutation
relays). Noted as a non-goal; addressable later on its own merits.

**No shared client / no middleware consolidation.** Four modules now carry a
near-identical `LedgerClientException` + `EnsureSuccessAsync` + `ReasonFrom`. That
duplication is a real candidate for a shared module-support layer or a shared base
`ILedgerClient` — but that is the deferred shared-SDK audit
([[accounting101-module-shared-sdk-audit]]), to be decided when a consolidation
seam is actually warranted, not forced here. This slice **duplicates the precedent
verbatim** (small, pure, per-module), consistent with the standing decision to
duplicate small per-module logic per brick until a consolidation trigger appears.

**No onboarding/startup chart validation.** Validating at client-onboarding time
that every module's folded control account is correctly dimensioned would prevent
the misconfiguration up front — but that is a larger, separate item (a
cross-module startup/onboarding validator). Deferred; this slice only ensures the
runtime read *relays* the fault cleanly.

**In scope:** `FixedAssets` + `Inventory` domain projects (new
`LedgerClientException.cs` each), their `.Api` `HttpLedgerClient`s (helpers + read
swaps), their `.Api` endpoint files (relay catch on the fold-reading reads), and
one E2E relay test per module.
**Out of scope:** the post/reverse/void relay gap, startup/onboarding validation,
shared-SDK consolidation, any engine change, any UI change, degrade-to-0 behavior.

## Architecture

### 1. Typed exception (new, per module — verbatim copy)

`Modules/FixedAssets/Accounting101.FixedAssets/LedgerClientException.cs` and
`Modules/Inventory/Accounting101.Inventory/LedgerClientException.cs`, each a
byte-for-byte port of the Receivables type (namespace adjusted to
`Accounting101.FixedAssets` / `Accounting101.Inventory`):

```csharp
public sealed class LedgerClientException(int statusCode, string reason)
    : Exception($"Ledger request failed ({statusCode}): {reason}")
{
    public int StatusCode { get; } = statusCode;
    public string Reason { get; } = reason;
}
```

Placed in the domain project (not `.Api`), matching AR/AP — the endpoints in
`.Api` reference it, and `.Api` already references the domain project.

### 2. `HttpLedgerClient` — add helpers, swap read methods

Port AR's two private static helpers **verbatim** into each module's
`HttpLedgerClient` (they are self-contained; `using System.Text;` and
`using System.Text.Json;` must be added):

- `EnsureSuccessAsync(HttpResponseMessage, CancellationToken)` — returns on
  success; otherwise reads the body and throws
  `LedgerClientException((int)response.StatusCode, ReasonFrom(body, response))`.
- `ReasonFrom(string body, HttpResponseMessage response)` — ValidationProblemDetails
  `errors` map (flattened) → ProblemDetails `detail` → raw body → status phrase.

Then, in each client, swap **only** the read methods from
`response.EnsureSuccessStatusCode();` to
`await EnsureSuccessAsync(response, cancellationToken);`:

- **FA** (`HttpLedgerClient.cs`): `GetEntriesBySourceRefAsync` (line 68),
  `GetSubledgerAsync` (line 82).
- **Inventory** (`HttpLedgerClient.cs`): `GetEntriesBySourceRefAsync` (line 68),
  `GetEntriesBySourceRefsAsync` (line 79), `GetSubledgerAsync` (line 93).

`PostAsync` / `ReverseAsync` / `VoidAsync` keep `EnsureSuccessStatusCode()`
unchanged (post-path non-goal, above).

### 3. Endpoints — relay catch on the fold-reading reads

The fold read is reached from the module's read endpoints via the service. Wrap
the fold-reading GETs in a try/catch that relays `LedgerClientException`, mirroring
the AR/AP catch verbatim:

```csharp
catch (LedgerClientException ex) // the engine refused the fold read (e.g. folded control account lacks its required dimension) — relay its real status + reason, not a 500
{
    return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
}
```

- **FA** (`FixedAssetsEndpoints.cs`): `GetAsset` (folds via `service.GetAsync`,
  line 104), `ListAssets` (folds via `service.GetByClientPagedAsync`, line 111).
- **Inventory** (`InventoryEndpoints.cs`): `GetItem` (folds via
  `service.GetAsync`, line 159), `ListItems` (folds via `service.GetPagedAsync`,
  line 166).

Only these four read endpoints fold a subledger. Other reads (runs, disposals,
movements) and the write endpoints are untouched. `ReactivateItem`
(`InventoryEndpoints.cs:148`) calls `service.GetAsync` to shape its response and
therefore also folds — but it is a write-path transition, and per the post-path
non-goal it is left as-is; its fold read can only fail on the same
misconfiguration and remains a pre-existing 500 exactly like the post path, out of
scope for this read-focused slice. (Documented here so the omission is deliberate,
not an oversight.)

## Data flow

```
Read endpoint (ListAssets/GetAsset, ListItems/GetItem)
  → service fold → ILedgerClient.GetSubledgerAsync → engine GET /subledger
       engine: folded control account lacks required dimension → 422 (or 404)
       → HttpLedgerClient.EnsureSuccessAsync throws LedgerClientException(422, reason)
       → endpoint catch relays Results.Problem(reason, statusCode: 422)   [clean 4xx, not 500]
```

## Error handling

- Fold read refusal (422 / 404 / any non-2xx) →
  `LedgerClientException(status, reason)` → relayed with the engine's own status
  code + reason.
- No degrade-to-0, no swallow, no runtime correction — the fault surfaces.

## Testing

E2E through the existing host fixtures (`FixedAssetsHostFixture`,
`InventoryHostFixture`; shared-Mongo infra [[accounting101-shared-test-mongo]]),
mirroring the AR/AP `LedgerErrorRelayE2eTests` shape but on the **read** path. One
new test file per module, pinning the exact smoke scenario.

- **FA relay** (`Modules/FixedAssets/.../LedgerErrorRelayE2eTests.cs`): seed a
  client; set up the chart but configure the Accumulated Depreciation account
  **without** the `"Asset"` required dimension (a plain `Asset`-type account, or
  one whose `RequiredDimensions` omits `"Asset"`) — every other account normal so
  asset creation succeeds. Create an asset (a Reference document; does not post, so
  it succeeds despite the misconfig). Then:
  - `GET /clients/{id}/assets/{assetId}` → assert status is **not** 500, is in
    `[400,499]` (specifically 422), and the `ProblemDetails.Detail` is non-empty
    (carries the engine's dimension-mismatch reason).
  - `GET /clients/{id}/assets` (list) → same assertions.
- **Inventory relay** (`Modules/Inventory/.../LedgerErrorRelayE2eTests.cs`): same
  shape — configure the Inventory Asset (`1400`) account **without** the `"Item"`
  required dimension, create an item, then:
  - `GET /clients/{id}/items/{itemId}` → not 500, is 422, non-empty detail.
  - `GET /clients/{id}/items` (list) → same.

Both assert the **pre-fix behavior is the bug** (these GETs 500 today) and the
**post-fix behavior is the relayed 422** — the exact `/assets` smoke crash, pinned.

## Success criteria

- No FA or Inventory list/detail read returns 500 when the folded control account
  is misconfigured; the engine's status (422/404) + reason are relayed as a clean
  4xx `ProblemDetails`.
- `LedgerClientException` exists in FixedAssets and Inventory, mirroring
  Receivables/Payables; the read methods use `EnsureSuccessAsync`; the four
  fold-reading endpoints relay it.
- The post/reverse/void path is unchanged (still `EnsureSuccessStatusCode`), and no
  degrade-to-0 behavior is introduced.
- Whole solution green; both module suites green; the two new relay tests fail
  before the client/endpoint change and pass after.

## Non-goals / Future Work (deferred — documented so the analysis isn't lost)

- **Post-path relay gap.** `PostAsync`/`ReverseAsync`/`VoidAsync` in all module
  `HttpLedgerClient`s still 500 on an engine rejection instead of relaying. A
  separate, module-wide slice (touches every module, adds write-path relay catches,
  interacts with module-credentialed mutation semantics). Not this change.
- **Startup / onboarding chart validation.** Validate at client-onboarding that
  each enabled module's folded control account is correctly dimensioned, so the
  422 is prevented rather than relayed. Larger cross-module item; deferred.
- **Shared module-support layer / shared base `ILedgerClient`.** The now-fourfold
  duplication of `LedgerClientException` + `EnsureSuccessAsync` + `ReasonFrom` is
  the trigger to revisit [[accounting101-module-shared-sdk-audit]] — but only when
  a genuine consolidation seam is warranted, not forced by this slice.
