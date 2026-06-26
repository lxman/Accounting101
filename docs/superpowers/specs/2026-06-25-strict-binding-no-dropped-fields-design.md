# Strict JSON binding — reject unknown/misnamed fields — Design

**Date:** 2026-06-25
**Status:** Spec for review
**Context:** The dog-food runs surfaced a class of silent data loss: the API **silently ignores misnamed/unknown JSON fields**. Three instances of one root cause —
- a controller POSTed `type: "Adjusting"` (the field name it saw on read-back); the request contract's field is `entryType`, so it was dropped → the entry stored as `Standard` (wrong classification, no error);
- a clerk POSTed `invoiceDate` (a nonexistent field); it was dropped → `issueDate`/`effectiveDate` defaulted to `0001-01-01`;
- the long-standing `?reference=` filter that did nothing.

A module or front end can throw a field at the engine, get a `2xx`, and have it silently not wire up. For an accounting system that is a correctness defect, not a convenience gap.

## Principle

**An input field is honored or rejected — never silently dropped.** This is the same rule already applied to the `reference` filter ("a filter must work or be rejected"), generalized to all request bodies. Two changes:

1. **Strict binding:** reject any JSON property that does not map to a contract member, with a clear `400` that names the offending field. One config change covers the whole Host (engine + Receivables + Payables — they share one `WebApplication`).
2. **Remove the one naming asymmetry** that the strict binding would otherwise turn into a recurring `400`: the entry-type field is `entryType` on the request but `type` on the response. Align the request to `type` so a read→modify→post round-trip uses one name.

## Approach

### Component 1 — strict binding (Host)

System.Text.Json ignores unmapped members by default. Configure the Host's HTTP JSON to **disallow** them:

```csharp
// Backend/Accounting101.Host/Program.cs (or a hosting extension)
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
```

`Disallow` makes deserialization throw `JsonException` ("The JSON property 'X' could not be mapped to any .NET member contained in type 'Y'.") on an unknown property. Because every endpoint (engine + modules) is mapped on the single `WebApplication` in `Program.cs`, this one line applies everywhere — so the `invoiceDate` footgun on the Receivables invoice-draft endpoint is caught too.

### Component 2 — surface a clear 400 (Host middleware)

Minimal-API body binding wraps the `JsonException` in a `BadHttpRequestException` (status 400). By default the response body does not carry the field name. Add early middleware to convert it to a ProblemDetails that does:

```csharp
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (BadHttpRequestException ex) when (ex.InnerException is JsonException je)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title  = "Invalid request body",
            Detail = je.Message,   // names the unmapped field, e.g. "...property 'invoiceDate' could not be mapped..."
        }, ctx.RequestAborted);
    }
});
```

Placed before `app.MapLedgerEndpoints()` etc. so it wraps all endpoints. (If the codebase already has an error-handling seam in `AddLedgerEngine`/`Hosting`, fold it there instead of `Program.cs` — implementer's call, but it must cover engine + modules.)

> The `je.Message` includes the unmapped property name and the target type — exactly what a module/front-end dev needs. We do not need to parse it; surfacing it is enough.

### Component 3 — align the entry-type field name (`entryType` → `type`)

`EntryResponse` exposes the type as `type`; `PostEntryRequest` accepts it as `entryType`. Rename the request member so both are `type`:

- `Backend/Accounting101.Ledger.Contracts/PostEntryRequest.cs`: rename the positional member `EntryType` → `Type` (wire field becomes `type`; update the doc comment).
- `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs`: `ParsePostableType(request.EntryType)` → `ParsePostableType(request.Type)`.
- Update any callers/tests constructing `PostEntryRequest` with a named `EntryType:` argument → `Type:` (positional constructions are unaffected).

After this, a caller who reads an entry (`type`) and posts it back with `type` works; the old `entryType` becomes an unmapped field and is rejected with the clear 400 (which is correct — it's the misnamed field we're eliminating). The modules do not set this field (they leave it null → Standard), so no module change is needed.

## Why this is the right blast radius

- **Internal engine + known module callers**, no external public API consumer — so rejecting unknown fields is safe and desirable; there is no forward-compatible client legitimately sending extra fields. (If that changes, `Disallow` can be relaxed per-type later.)
- It retires the **whole class** at the boundary: `type`/`entryType`, `invoiceDate`/`date`, and any future misnamed field all become a clear `400` instead of silent loss. The inception date floor (already shipped) remains the downstream catch for a genuinely-bad-but-well-named date; strict binding catches the *misnamed* date field at the door.

## Testing

API (`Accounting101.Ledger.Api.Tests`, and one module test if cheap):

- **Unknown field rejected:** `POST /entries` with an extra/misnamed property (e.g. `"entryType"` after the rename, or any `"bogus": 1`) → `400` whose body Detail names the field.
- **Misnamed date field rejected:** a Receivables invoice draft (or an engine entry) carrying `invoiceDate`/`date` → `400` naming it (proves the footgun's root is now caught, not silently defaulted). If wiring a module test is heavy, assert the engine equivalent and note the module is covered by the same global config.
- **Well-formed request still works:** a valid `PostEntryRequest` (typed) posts `201` as before — strict binding does not reject mapped fields.
- **Entry type honored after alignment:** posting with `type: "Adjusting"` stores `type == "Adjusting"` (read it back); `type: "Standard"`/omitted → Standard. (This is the regression the run exposed — the controller's Adjusting entries stored Standard.)
- **Full-suite regression:** run the engine Api tests, the Mongo tests (where they post via the API host), and the Receivables/Payables module tests. Any test that posts a body with an unmapped/extra field will now `400` — fix those test payloads (they were relying on the silent-ignore). Report every test touched and confirm none was a real product behavior.

## Scope

**In scope:** the `ConfigureHttpJsonOptions(Disallow)` config, the 400-surfacing middleware, the `entryType → type` rename + its callsites, and the tests above (including fixing any existing test that leaned on silent-ignore).

**Out of scope (documented):**
- A fully structured field-level `errors[]` array (the broader "actionable parse errors" group-3 item) — this slice surfaces the JsonException message, which already names the field; a richer multi-error contract is a separate ergonomics slice.
- Draft-delete / draft-edit lifecycle (the stranded-Draft ergonomics gap) — separate small slice.
- Relaxing `Disallow` per-type for a future forward-compatible external client — not needed now.

## Global constraints

- .NET 10; build 0 warnings; commit per slice; TDD.
- One global config covers engine + modules (single `WebApplication`). The 400 must name the offending field.
- Reject unknown fields; honor mapped ones unchanged. No silent drops.
