# Module idempotency wiring — Design

**Date:** 2026-06-25
**Status:** Spec for review
**Context:** The engine now makes `POST /entries` idempotent on a caller-supplied `Id` (merge `52b696b`), but the primitive is inert until callers send a **stable, operation-scoped** id. Today the Receivables and Payables modules compose their posts with `Id: null`, so the engine mints a fresh `Guid` every time and a retried post duplicates. This slice has the modules derive a deterministic entry `Id` from the operation, so a retried module post replays the existing entry instead of duplicating.

## Principle

The engine honors a caller-declared identity; the module's job is to **declare the same identity on every retry of the same operation**. A module that does `Id: null` (or `Guid.NewGuid()`) declares "a new operation" each time — by its own assertion — so nothing can dedup it. The fix is a pure, deterministic derivation: same operation ⇒ same `Id`.

## What is the operation key?

Each module posting already carries its natural identity:
- `SourceRef` — the business-document id (invoice id, bill id, bill-payment id, vendor-credit-application id), a stable `Guid` assigned when the document is created.
- `SourceType` — a distinct discriminator per posting kind (`"Invoice"`, `"Bill"`, `"BillPayment"`, `"VendorCreditApplication"`).

At the module layer, `(SourceType, SourceRef)` is **operation-unique**: each document spawns exactly one *original* journal entry, and reversals/voids are **engine-generated** (the void path calls `ledger.ReverseAsync`/`VoidAsync(entryId)` — it does not compose a new `PostEntryRequest` with a module-chosen `Id`). So no `purpose` discriminator is needed here.

> Caveat (documented, not built): if a module ever composes a *revision* of an existing document with its own `Id` (reusing `SourceRef`), it must add a purpose suffix to the key so the revision does not collide with the original. No module does this today.

## Derivation: a deterministic name-based (UUIDv5) GUID

`Id = EntryIdentity.ForSource(SourceType, SourceRef)` where `ForSource` is an RFC-4122 **version-5** (SHA-1, name-based) UUID over a fixed Accounting101 namespace and the name `"{SourceType}:{SourceRef:N}"`. Properties:
- **Deterministic:** same `(SourceType, SourceRef)` ⇒ same `Guid`, every call, every process — the whole point.
- **Collision-resistant:** different `SourceType` or `SourceRef` ⇒ different `Guid` (distinct names under one namespace).
- **Stateless & pure:** no storage, no clock, no RNG.

### Component 1 — `EntryIdentity` (in `Accounting101.Ledger.Contracts`)

Placed in Contracts because both modules already reference it and it concerns the entry wire contract's `Id`.

```csharp
namespace Accounting101.Ledger.Contracts;

/// <summary>
/// Deterministic entry ids for module postings, so a retried post is idempotent against the engine's
/// caller-supplied-id dedup. The id is a name-based (UUIDv5) GUID over the operation's (SourceType,
/// SourceRef) — same operation ⇒ same id, every time; different operation ⇒ different id.
/// </summary>
public static class EntryIdentity
{
    // Fixed namespace for Accounting101 ledger-entry identities (generate once, hardcode).
    private static readonly Guid Namespace = Guid.Parse("b3f1d6c2-7a4e-5b9c-8d0f-1e2a3b4c5d6e");

    public static Guid ForSource(string sourceType, Guid sourceRef)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceType);
        return Uuid5(Namespace, $"{sourceType}:{sourceRef:N}");
    }

    // RFC 4122 §4.3 name-based (SHA-1) UUID. Standard, well-known algorithm.
    private static Guid Uuid5(Guid ns, string name) { /* SHA1(ns-bytes big-endian || utf8(name)),
        take 16 bytes, set version=5 and variant=RFC4122, return as a .NET Guid with correct byte order */ }
}
```

> Implementer: implement `Uuid5` correctly — the namespace bytes must be in **big-endian (network) order** before hashing (the .NET `Guid.ToByteArray()` is little-endian for the first three fields), and the result's version/variant nibbles set per RFC 4122, then converted back to a .NET `Guid`. Pin the output with a known test vector (e.g. a fixed namespace + name → a fixed expected GUID) so the algorithm can't silently drift.

### Component 2 — set the id in the four `Compose*` functions

- `Modules/Receivables/Accounting101.Receivables/InvoicePosting.cs` — `Compose`: `Id: EntryIdentity.ForSource(SourceType, invoice.Id)` (replacing `Id: null`). `SourceType` is the existing `"Invoice"` const; `SourceRef` stays `invoice.Id`.
- `Modules/Payables/Accounting101.Payables/BillPosting.cs` — `ComposeBill` → `ForSource(BillSourceType, bill.Id)`; `ComposeBillPayment` → `ForSource(BillPaymentSourceType, paymentId)`; `ComposeVendorCreditApplication` → `ForSource(VendorCreditApplicationSourceType, id)`.

Nothing else changes: `SourceRef`/`SourceType`/lines/dates are untouched, so the composed entry is identical except it now carries a stable id.

## Why this is safe with the existing flows

- **Receivables `IssueAsync` composes twice** (`InvoiceService.cs:62` pre-flight `ValidateAsync`, `:69` the real `PostAsync`). Both now carry the same deterministic id. `ValidateAsync` writes nothing (the `/entries/validate` dry-run ignores id), so the only write is the post. A retry that re-reaches the post re-derives the same id ⇒ engine returns the existing entry (200), no duplicate. (The module's existing `Status == Draft` guard still blocks re-issuing an already-issued invoice earlier; idempotency is the backstop for the finalize-succeeded-post-failed window and any direct re-post.)
- **Void/reverse paths unaffected:** they call `ledger.ReverseAsync`/`VoidAsync(entryId)` — engine-generated ids, never `EntryIdentity`.
- **No behavior change on the happy path:** the first post still creates the entry; only its id is now derived instead of random.

## Testing

`EntryIdentity` (pure unit, `Accounting101.Ledger.Contracts.Tests` — create if absent):
- **Deterministic:** `ForSource("Invoice", g)` equals itself across calls.
- **Distinct on type:** `ForSource("Invoice", g) != ForSource("Bill", g)`.
- **Distinct on ref:** `ForSource("Invoice", g1) != ForSource("Invoice", g2)`.
- **Known vector:** a fixed `(sourceType, sourceRef)` yields a pinned expected `Guid` (guards the UUIDv5 algorithm against drift), and the GUID's version nibble is `5` and variant is RFC-4122.
- Null/empty `sourceType` throws.

Modules:
- `InvoicePosting.Compose` / `BillPosting.Compose*` now set `Id == EntryIdentity.ForSource(<type>, <ref>)` (assert on the composed `PostEntryRequest.Id`), and re-composing the same document yields the same id (update existing posting tests; they currently assert `Id` is null or ignore it).
- E2e (one per module, against the real engine + EphemeralMongo if the module test harness supports it; otherwise a fake `ILedgerClient` that records posted ids): posting the same invoice/bill-payment operation twice results in **one** engine entry (the second is an idempotent replay), proving the wiring closes the duplicate path end-to-end. If the module test harness uses a fake ledger client, assert the two composes produce the same id and document the engine-side dedup is covered by the engine's own idempotency tests.

## Scope

**In scope:** `EntryIdentity.ForSource` (+ UUIDv5) in Contracts; setting the derived id in the four `Compose*` functions; the tests above.

**Out of scope (separate, documented):**
- **Bill-creation idempotency** at the Payables document store (`POST /bills` minting a duplicate *bill document*) — that is a document-store dedup, a different surface from the journal-entry post; the month-8 double-pay traces there.
- **Plain-JE callers** (the clerk/harness posting LOAN/PR/TAXPAY directly via `POST /entries`) — a client/UI concern: a real UI would derive a stable id per source document. The harness duplicate is not a product defect.
- A `purpose`-discriminated key (only needed if a module later composes its own revisions).

## Global constraints

- .NET 10; build 0 warnings; commit per slice; TDD.
- `EntryIdentity` is pure and deterministic — no storage, clock, or RNG. The UUIDv5 algorithm is pinned by a known-vector test.
- Domain-agnostic in Contracts; modules only change which value they pass for `Id`.
