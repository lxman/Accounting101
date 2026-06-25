# Module idempotency wiring — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Have the Receivables and Payables modules derive a deterministic entry `Id` from `(SourceType, SourceRef)`, so a retried module post is an idempotent replay against the engine (no duplicate).

**Architecture:** A pure `EntryIdentity.ForSource(sourceType, sourceRef)` (UUIDv5) in Contracts produces a stable id; the four `Compose*` functions set it instead of `Id: null`. No engine change (the idempotency primitive already shipped).

**Tech Stack:** C#/.NET 10; xUnit; pure unit tests + module posting tests.

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- `EntryIdentity` is pure/deterministic (no storage/clock/RNG); the UUIDv5 algorithm is pinned by a known-vector test.
- Only the `Id` value passed by the modules changes — `SourceRef`/`SourceType`/lines/dates are untouched.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; check for stray churn (linter may rewrite types to `var`).

---

## Task 1: `EntryIdentity.ForSource` (deterministic UUIDv5)

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/EntryIdentity.cs`
- Test: `Backend/Accounting101.Ledger.Contracts.Tests/EntryIdentityTests.cs` (create the test project if it does not exist — minimal xUnit project referencing Contracts; mirror an existing `*.Tests.csproj`)

**Interfaces:**
- Produces (consumed by Task 2): `public static Guid Accounting101.Ledger.Contracts.EntryIdentity.ForSource(string sourceType, Guid sourceRef)`

- [ ] **Step 1: Write the failing tests**

```csharp
public class EntryIdentityTests
{
    static readonly Guid G1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid G2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact] public void Deterministic()        => Assert.Equal(EntryIdentity.ForSource("Invoice", G1), EntryIdentity.ForSource("Invoice", G1));
    [Fact] public void Distinct_on_type()     => Assert.NotEqual(EntryIdentity.ForSource("Invoice", G1), EntryIdentity.ForSource("Bill", G1));
    [Fact] public void Distinct_on_ref()      => Assert.NotEqual(EntryIdentity.ForSource("Invoice", G1), EntryIdentity.ForSource("Invoice", G2));

    [Fact]
    public void Is_rfc4122_version5()
    {
        byte[] b = EntryIdentity.ForSource("Invoice", G1).ToByteArray();
        // After Guid round-trip the version/variant live at specific positions; assert via the canonical string instead:
        string s = EntryIdentity.ForSource("Invoice", G1).ToString();
        Assert.Equal('5', s[14]);                 // version nibble
        Assert.Contains(s[19], "89ab");           // RFC4122 variant nibble
    }

    [Fact]
    public void Known_vector_pins_the_algorithm()
    {
        // Compute ONCE with the final implementation, then hardcode here so the algorithm cannot drift.
        Assert.Equal(Guid.Parse("<FILL FROM FIRST GREEN RUN>"), EntryIdentity.ForSource("Invoice", G1));
    }

    [Fact] public void Null_or_empty_type_throws() => Assert.ThrowsAny<ArgumentException>(() => EntryIdentity.ForSource("", G1));
}
```

> Implementer: for `Known_vector_pins_the_algorithm`, run the impl once, read the produced GUID, and hardcode it — this is the regression pin, not a value to invent.

- [ ] **Step 2: Run, confirm fail** (`dotnet test Backend/Accounting101.Ledger.Contracts.Tests --filter EntryIdentityTests` → does not compile).

- [ ] **Step 3: Implement** `EntryIdentity` per the spec. Correct UUIDv5: hash `SHA1( bigEndian(namespace 16 bytes) || utf8(name) )`; take the first 16 bytes; set the version nibble to `5` (byte 6 high nibble) and the variant (byte 8 top two bits to `10`); convert those 16 **RFC-order** bytes back to a .NET `Guid` (account for .NET's mixed-endian `Guid(byte[])` constructor — the first three fields are little-endian). Name = `$"{sourceType}:{sourceRef:N}"`.

- [ ] **Step 4: Run, confirm pass** (fill the known-vector GUID first). → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Contracts/EntryIdentity.cs Backend/Accounting101.Ledger.Contracts.Tests/
git commit -m "feat(contracts): EntryIdentity.ForSource — deterministic UUIDv5 entry ids for idempotent module posts"
```

---

## Task 2: Set the derived id in the four `Compose*` functions

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/InvoicePosting.cs` (`Compose`)
- Modify: `Modules/Payables/Accounting101.Payables/BillPosting.cs` (`ComposeBill`, `ComposeBillPayment`, `ComposeVendorCreditApplication`)
- Test: update `Modules/Receivables/Accounting101.Receivables.Tests/*` and `Modules/Payables/Accounting101.Payables.Tests/*` posting tests

**Interfaces:**
- Consumes: `EntryIdentity.ForSource` (Task 1).

- [ ] **Step 1: Write/adjust the failing tests**

For each `Compose*`, assert the composed `PostEntryRequest.Id == EntryIdentity.ForSource(<SourceType>, <ref>)` and that re-composing the same document yields the same id. Add to the existing posting test files (e.g. Receivables `InvoicePostingTests`, Payables `BillPostingTests` / `BillPaymentTypesTests`). If an existing test asserted `Id` is null, update it to the new expectation.

```csharp
[Fact]
public void Compose_sets_a_deterministic_id_from_source()
{
    PostEntryRequest a = InvoicePosting.Compose(invoice, accounts);
    PostEntryRequest b = InvoicePosting.Compose(invoice, accounts);
    Assert.Equal(EntryIdentity.ForSource("Invoice", invoice.Id), a.Id);
    Assert.Equal(a.Id, b.Id); // stable across composes
}
```

- [ ] **Step 2: Run, confirm fail** (`Id` is currently null) — run the affected posting test classes.

- [ ] **Step 3: Implement** — in each `Compose*`, replace `Id: null` with `Id: EntryIdentity.ForSource(<SourceTypeConst>, <ref>)`:
  - `InvoicePosting.Compose`: `Id: EntryIdentity.ForSource(SourceType, invoice.Id)`
  - `BillPosting.ComposeBill`: `Id: EntryIdentity.ForSource(BillSourceType, bill.Id)`
  - `BillPosting.ComposeBillPayment`: `Id: EntryIdentity.ForSource(BillPaymentSourceType, paymentId)`
  - `BillPosting.ComposeVendorCreditApplication`: `Id: EntryIdentity.ForSource(VendorCreditApplicationSourceType, id)`
  Add `using Accounting101.Ledger.Contracts;` where needed (likely already present).

- [ ] **Step 4: Run, confirm pass** — affected posting test classes green. Also run the existing Receivables/Payables service + e2e test classes to confirm no regression (the issue/bill-payment flows still post correctly; the id is now set, which is inert on a first post).

- [ ] **Step 5: Build clean, commit**
```bash
git add Modules/Receivables/Accounting101.Receivables/InvoicePosting.cs Modules/Payables/Accounting101.Payables/BillPosting.cs <touched test files>
git commit -m "feat(modules): derive deterministic entry ids in Receivables/Payables posts (idempotent retry)"
```

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: `EntryIdentityTests`, Receivables posting/service/e2e classes, Payables posting/service/e2e classes — all green.
- [ ] Confirm: only the `Id` value changed in the composes; `SourceRef`/`SourceType`/lines/dates untouched.
- [ ] (single-area change) task reviews serve as the gate; a brief controller spot-check of the diff suffices before finishing.

## Self-review (author)
- Spec coverage: deterministic id (Task 1 tests), all four composes wired (Task 2), known-vector pin (Task 1), no-regression on flows (Task 2 Step 4).
- Type consistency: `EntryIdentity.ForSource(string, Guid) → Guid` used identically across both tasks and both modules.
- Open implementer check: the UUIDv5 byte-order/endianness (flagged in Task 1 Step 3) and the known-vector pin (Task 1 Step 1).
