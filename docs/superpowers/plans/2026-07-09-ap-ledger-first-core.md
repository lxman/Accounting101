# AP Ledger-First Core — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the ledger the single source of truth for Payables — every A/P balance becomes a fold over `{Vendor, Bill}`-dimensioned journal lines, and the module stores no monetary amount or allocation array.

**Architecture:** A mechanical port of the just-shipped AR ledger-first core (`docs/superpowers/plans/2026-07-09-ar-ledger-first-core.md`, on master) to Payables. The engine already has everything (`RequiredDimensions` set + enforcement, `AggregateSubledgerAsync(..., includePending)`), so there is NO engine change. AP's two A/P relievers (bill payment, vendor credit application) plus bill enter emit `{Vendor, Bill}`-dimensioned A/P lines; reads fold the ledger; `Allocation[]` storage is deleted. Staged so the module stays green at each commit: recipes gain the `Bill` tag additively → A/P's requirement flips on → reads fold → allocation storage removed.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB, xUnit, `WebApplicationFactory<Program>` host fixtures, EphemeralMongo (`SharedMongo`).

## Global Constraints

- **Dimension keys are exactly** `"Vendor"` and `"Bill"`. A/P lines must carry both. `BillPosting` already has `VendorDimension = "Vendor"`; add `BillDimension = "Bill"`.
- **No engine change.** Reuse the shipped `RequiredDimensions` set, post-time enforcement, and `AggregateSubledgerAsync(..., includePending)`. Do not modify `Backend/`.
- **⭐ SIGN DISCIPLINE — the top risk. Do NOT copy AR's signs.** A/P is a **liability (credit-normal)**; `AggregateSubledgerAsync` is debit-positive (Dr − Cr). A bill's A/P line is a Credit (bill enter) offset by Debits (payments), so `fold(Bill=B) = −(open balance)` → **`open = −fold`**. Vendor Credits is an **Asset (debit-normal)**; its available-credit fold reads **directly positive → NO negation** (AR negated because Customer Credits is a liability). Every fold sign MUST be pinned by a test, not assumed.
- **Over-application validation is pending-inclusive** (`includePending: true`); all balance/aging/view reads are Posted-only (`includePending: false`). This is the "writes see pending claims, reads see only posted" principle the AR work established — build it in from the start (AR had to retrofit it).
- **Greenfield:** no historical AP data preserved; dev/demo reseeded. No backfill.
- **Required chart config** (mirroring AR's smoke finding): A/P → `{Vendor, Bill}`, Vendor Credits → `{Vendor}`.
- **Commit trailer on every commit:** `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`. Stage explicit paths only, never `-A`. Do NOT stage `UI/Angular/src/app/core/api/environment.ts`.
- Spec: `docs/superpowers/specs/2026-07-09-ap-ledger-first-core-design.md`.

## File structure (what each task touches)

- **Read client (Task 1):** `Modules/Payables/Accounting101.Payables/ILedgerClient.cs`, `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`.
- **Recipes (Tasks 2–4):** `Modules/Payables/Accounting101.Payables/BillPosting.cs`.
- **Enforcement flip (Task 5):** the Payables test fixture / `SetUpChartAsync` / `PutAccountAsync` helpers; prod seeding note.
- **Reads (Task 6):** `Modules/Payables/Accounting101.Payables/SettlementRelief.cs` (new), `VendorAccountService.cs` (the vendor-360 assembler, `GetAccountAsync(clientId, vendorId, asOf, ct) → VendorAccountView`; ctor is `VendorAccountService(IVendorStore vendors, IBillStore bills, IBillPaymentStore payments)` — Task 6 ADDS an `ILedgerClient` ctor param, registered `AddScoped<VendorAccountService>()` in `PayablesServiceExtensions.cs`), `BillPaymentService.cs`, `VendorAccountBuilder.cs` (Statement/CreditActivity signatures).
- **Storage deletion (Task 7):** `Modules/Payables/Accounting101.Payables/BillPayment.cs`, `BillPaymentBody.cs`, `BillPaymentService.cs`, `VendorAccountBuilder.cs`.
- **Proof suite (Task 8):** `Modules/Payables/Accounting101.Payables.Tests/ApLedgerFirstProofTests.cs` (new).

---

### Task 1: Payables ledger client — add the subledger read-fold method

Add `GetSubledgerAsync` to the Payables `ILedgerClient` + `HttpLedgerClient`, verbatim analog of the Receivables version already on master.

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/ILedgerClient.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/SubledgerReadTests.cs` (new)

**Interfaces:**
- Consumes: engine `GET /clients/{clientId}/subledger?account=&dimension=&asOf=[&includePending=]` → `SubledgerResponse(string Dimension, DateOnly? AsOf, IReadOnlyList<SubledgerLineResponse> Lines)`, `SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance)` (in `Accounting101.Ledger.Contracts`).
- Produces: `ILedgerClient.GetSubledgerAsync(Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default, bool includePending = false) → Task<IReadOnlyList<SubledgerLineResponse>>`. Consumed by Task 6.

- [ ] **Step 1: Read the reference implementation.** Open `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs` and `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs` (on master) — copy the exact `GetSubledgerAsync` interface signature and HTTP implementation shape (the `Forwarded(...)`/`SendAsync` bearer-forwarding convention, the `includePending` query-param handling, `SubledgerResponse` deserialization returning `.Lines`). The Payables `HttpLedgerClient` uses the same `Forwarded(...)` helper.

- [ ] **Step 2: Write the failing test.** Create `Modules/Payables/Accounting101.Payables.Tests/SubledgerReadTests.cs`, mirroring `Modules/Receivables/Accounting101.Receivables.Tests/SubledgerReadTests.cs`. Use `PayablesHostFixture`: enter+approve a bill (which posts a `{Vendor}`-tagged A/P line today), resolve `ILedgerClient` from `fixture.Services` (DI scope with a populated auth header, exactly as the Receivables test does), and assert the `Vendor`-axis A/P fold for that vendor equals the bill total (with the correct sign — A/P is credit-normal, so the fold reads negative; assert `-line.Balance == billTotal` OR assert the magnitude and document the sign). Query dimension `"Vendor"` (the `Bill` tag arrives in Task 2).

- [ ] **Step 3: Run to verify it fails.** Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~SubledgerRead"`. Expected: FAILS to compile (`GetSubledgerAsync` undefined).

- [ ] **Step 4: Add the interface method** to `ILedgerClient.cs`:
```csharp
    /// <summary>Read a per-dimension control-account fold: the signed (debit-positive) balance of
    /// <paramref name="account"/> grouped by the value of dimension <paramref name="dimension"/>
    /// (e.g. "Vendor" or "Bill"). Set <paramref name="includePending"/> to also count PendingApproval
    /// entries (for write-side over-application validation); leave false for Posted-only reads.</summary>
    Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf,
        CancellationToken cancellationToken = default, bool includePending = false);
```
Add `using Accounting101.Ledger.Contracts;` if needed.

- [ ] **Step 5: Implement it** in `HttpLedgerClient.cs`, mirroring the Receivables implementation exactly (build `clients/{clientId}/subledger?account={account}&dimension={Uri.EscapeDataString(dimension)}`, append `&asOf={d:yyyy-MM-dd}` when set and `&includePending=true` when set, use the file's `Forwarded(...)` + `SendAsync` + existing error-relay helper, deserialize `SubledgerResponse`, return `.Lines`). Add a matching stub to the test `FakeLedgerClient` (in the Payables tests' `Fakes.cs` if one exists) so the project compiles — mirror the Receivables fake's `GetSubledgerAsync` (it may fold posted entries or return empty; copy whatever the Receivables fake does).

- [ ] **Step 6: Run to verify pass.** Run the Step 3 filter. Expected: PASS.

- [ ] **Step 7: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables/ILedgerClient.cs \
        Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs \
        Modules/Payables/Accounting101.Payables.Tests/SubledgerReadTests.cs \
        <Payables tests Fakes.cs if changed>
git commit -m "feat(payables): ledger client can read a per-dimension subledger fold

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Bill-enter recipe tags the Bill dimension (additive)

Add `Bill = bill.Id` to the `Cr A/P` line. A/P still requires only `Vendor` (flipped in Task 5), so the tag is additive and existing tests stay green.

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/BillPosting.cs` (`ComposeBill`)
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillDimensionTests.cs` (new)

**Interfaces:**
- Produces: entered-bill A/P line carrying `{Vendor=bill.VendorId, Bill=bill.Id}`; `BillPosting.BillDimension = "Bill"`.

- [ ] **Step 1: Write the failing test.** Create `Modules/Payables/Accounting101.Payables.Tests/BillDimensionTests.cs`. Mirror `PayablesE2eTests`: seed SoD client, `SetUpChartAsync` (A/P still `RequiredDimension="Vendor"` — do NOT flip here), create a vendor, enter a bill (with a line), read the spawned entry via `GET /entries?sourceRef={billId}`, approve it, and assert the A/P line carries BOTH `Dimensions["Vendor"] == vendorId` AND `Dimensions["Bill"] == billId`. Also assert the `Bill`-axis fold via the ungated bare `GET /clients/{clientId}/subledger?dimension=Bill` (NOT the reconciliation endpoint — it 422s until A/P requires Bill in Task 5) returns the bill's line for that bill.

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillDimension"`. Expected: FAILS (`Dimensions["Bill"]` absent).

- [ ] **Step 3: Tag the Bill dimension.** In `BillPosting.cs`, add the const beside `VendorDimension`:
```csharp
    public const string BillDimension = "Bill";
```
and change the A/P credit line in `ComposeBill` to include the bill id:
```csharp
        lines.Add(new(accounts.PayableAccountId, "Credit", bill.Total,
            Dimensions: new Dictionary<string, Guid>
            {
                [VendorDimension] = bill.VendorId,
                [BillDimension] = bill.Id,
            }));
```
Leave the expense debit lines untagged.

- [ ] **Step 4: Run to verify pass.** Run the Step 2 filter. Expected: PASS.

- [ ] **Step 5: Run the whole Payables suite.** Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`. Expected: all PASS (additive; A/P still requires only Vendor).

- [ ] **Step 6: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables/BillPosting.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillDimensionTests.cs
git commit -m "feat(payables): bill enter tags the AP line with Bill dimension

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Bill-payment recipe emits one dimensioned A/P line per allocation

Replace the single aggregate `Dr A/P` line with one `Dr A/P {Vendor, Bill=TargetId}` line per allocation. `Allocation[]` still stored; A/P still requires only Vendor.

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/BillPosting.cs` (`ComposeBillPayment`)
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillPaymentDimensionTests.cs` (new); update any existing test asserting a single aggregate A/P line.

**Interfaces:**
- Produces: a bill-payment entry with N `Dr A/P` lines, line i = `{Vendor=body.VendorId, Bill=allocations[i].TargetId}` amount `allocations[i].Amount`, plus the unchanged `Dr Vendor Credits {Vendor}` remainder and `Cr Cash` (full amount).

- [ ] **Step 1: Write the failing test.** Create `BillPaymentDimensionTests.cs`. Seed a client, enter+approve two bills (A total 100, B total 100) for one vendor, record ONE payment of 150 split allocations `[(billA, 100), (billB, 50)]`, approve the payment entry, read via `GET /entries?sourceRef={paymentId}`, and assert: exactly TWO A/P Debit lines, one `{Vendor, Bill=A}` amount 100, the other `{Vendor, Bill=B}` amount 50. Then assert each bill's `Bill`-axis fold via the ungated bare `GET /subledger?dimension=Bill`: bill A open 0, bill B open 50 (remember `open = −fold`; assert the fold value gives open 0 / 50 with the correct sign — pin it here).

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test ... --filter "FullyQualifiedName~BillPaymentDimension"`. Expected: FAILS (one aggregate A/P line today).

- [ ] **Step 3: Rewrite `ComposeBillPayment`.** In `BillPosting.cs`:
```csharp
    public static PostEntryRequest ComposeBillPayment(Guid paymentId, BillPaymentBody body, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal allocated = body.Allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;

        List<PostLineRequest> lines = [];
        foreach (Allocation a in body.Allocations)
        {
            if (a.Amount == 0m) continue;
            lines.Add(new(accounts.PayableAccountId, "Debit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [VendorDimension] = body.VendorId,
                    [BillDimension] = a.TargetId,
                }));
        }
        if (remainder != 0m)
            lines.Add(new(accounts.VendorCreditsAccountId, "Debit", remainder,
                Dimensions: new Dictionary<string, Guid> { [VendorDimension] = body.VendorId }));
        lines.Add(new(accounts.CashAccountId, "Credit", body.Amount));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(BillPaymentSourceType, paymentId), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: paymentId, SourceType: BillPaymentSourceType);
    }
```
(`Allocation` is `Accounting101.Settlement.Allocation`; the file already `using`s it.)

- [ ] **Step 4: Update any broken existing test.** Run the whole Payables suite (Step 5). Any existing test asserting a single aggregate `Dr A/P` line of `allocated` now sees N per-allocation lines — update those assertions to the per-allocation shape (per-line amounts + `Bill` tags summing to the same allocated total). Do NOT weaken. Vendor-axis assertions remain valid (every A/P line still carries `Vendor`).

- [ ] **Step 5: Run the whole Payables suite.** Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`. Expected: all PASS.

- [ ] **Step 6: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables/BillPosting.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillPaymentDimensionTests.cs \
        <any updated existing test files>
git commit -m "feat(payables): bill payment emits one Bill-dimensioned AP line per allocation

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Vendor-credit-application recipe emits dimensioned A/P lines

Apply the same per-allocation change to `ComposeVendorCreditApplication` (the only AP disposition). `Allocation[]` still stored.

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/BillPosting.cs` (`ComposeVendorCreditApplication`)
- Test: `Modules/Payables/Accounting101.Payables.Tests/VendorCreditApplicationDimensionTests.cs` (new); update any existing aggregate-line test.

**Interfaces:**
- Produces: a vendor-credit-application entry with one `Dr A/P {Vendor, Bill=TargetId}` line per allocation and one `Cr Vendor Credits {Vendor}` line for the total.

- [ ] **Step 1: Write the failing test.** Create `VendorCreditApplicationDimensionTests.cs`. Seed a client; establish vendor credit by recording a bill-payment overpayment (pay 100 with 40 allocated to a bill, 60 remainder → 60 vendor credit) and approving it; enter+approve a second bill (100); then record a vendor-credit-application of 60 allocated to the second bill; approve it; read via `GET /entries?sourceRef={vcaId}` and assert one `Dr A/P {Vendor, Bill=secondBill}` line of 60 and one `Cr Vendor Credits {Vendor}` line of 60. Assert the second bill's `Bill`-axis fold shows open reduced by 60 (open 40) via the bare `?dimension=Bill`.

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test ... --filter "FullyQualifiedName~VendorCreditApplicationDimension"`. Expected: FAILS (aggregate line today).

- [ ] **Step 3: Rewrite `ComposeVendorCreditApplication`.** In `BillPosting.cs`:
```csharp
    public static PostEntryRequest ComposeVendorCreditApplication(Guid id, VendorCreditApplicationBody body, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal applied = body.Allocations.Sum(a => a.Amount);

        List<PostLineRequest> lines = [];
        foreach (Allocation a in body.Allocations)
        {
            if (a.Amount == 0m) continue;
            lines.Add(new(accounts.PayableAccountId, "Debit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [VendorDimension] = body.VendorId,
                    [BillDimension] = a.TargetId,
                }));
        }
        lines.Add(new(accounts.VendorCreditsAccountId, "Credit", applied,
            Dimensions: new Dictionary<string, Guid> { [VendorDimension] = body.VendorId }));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(VendorCreditApplicationSourceType, id), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: id, SourceType: VendorCreditApplicationSourceType);
    }
```

- [ ] **Step 4: Update any broken existing test** to the per-allocation shape (do not weaken). Run the whole Payables suite (Step 5).

- [ ] **Step 5: Run the whole Payables suite.** Expected: all PASS.

- [ ] **Step 6: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables/BillPosting.cs \
        Modules/Payables/Accounting101.Payables.Tests/VendorCreditApplicationDimensionTests.cs \
        <any updated existing test files>
git commit -m "feat(payables): vendor credit application relieves bills via dimensioned AP lines

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Flip A/P to require {Vendor, Bill}

Now that every A/P-relieving recipe tags `Bill`, make A/P require both dimensions so an untagged (unfoldable) A/P line is rejected 422. Vendor Credits already requires `{Vendor}` — leave it.

**Files:**
- Modify: every A/P account setup in the Payables tests (`SetUpChartAsync` + the `PutAccountAsync` helper in `PayablesE2eTests.cs` and any sibling test/fixture that PUTs the A/P account).
- Investigate: onboarding / `OpenAsync` for any posted A/P opening-balance line (as AR did — likely none; report).
- Test: `Modules/Payables/Accounting101.Payables.Tests/ApRequiresBillTests.cs` (new).

**Interfaces:**
- Consumes: `AccountRequest.RequiredDimensions` (shipped with AR); recipes now emit the Bill tag (Tasks 2–4).
- Produces: A/P configured `{Vendor, Bill}`; a proof that a raw A/P line missing Bill is rejected 422.

- [ ] **Step 1: Write the failing enforcement-proof test.** Create `ApRequiresBillTests.cs`: PUT A/P with `RequiredDimensions = ["Vendor", "Bill"]`, PUT an expense account, then POST a hand-built entry with an A/P Credit line tagged `Vendor`-only (no Bill), assert 422 and the body contains "Bill".

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test ... --filter "FullyQualifiedName~ApRequiresBill"`. Expected: FAILS (A/P in that test's own setup still requires only Vendor → the Vendor-only line posts OK).

- [ ] **Step 3: Flip every A/P account setup.** The shared helper is `PutAccountAsync(http, clientId, accountId, number, name, type, requiredDimension)` (a single legacy string). Change it (and its call sites for A/P) to pass the set. Simplest: change the helper signature to accept `params string[] requiredDimensions` and build `new AccountRequest { ..., RequiredDimensions = requiredDimensions }`; update the A/P call to `..., "Liability", "Vendor", "Bill"` and the other calls to their single dimension or none. Search the Payables test project for every place the A/P account (`fixture.PayableAccountId`) is PUT and ensure all now require `["Vendor", "Bill"]`. Do NOT change Vendor Credits (stays `["Vendor"]`) or non-dimensioned accounts.

- [ ] **Step 4: Investigate onboarding.** Search the engine `Onboard` handler / any Payables seed for a posted A/P opening-balance line. If none, note it in the report — no change. If one exists, give its A/P line a `Bill` dimension (opening-balance pseudo-bill) and add a focused test.

- [ ] **Step 5: Run the whole Payables suite.** Expected: all PASS — every recipe now supplies the Bill tag; the enforcement-proof test passes. **If any test fails, a recipe path still emits an untagged A/P line — fix the recipe, not the requirement.**

- [ ] **Step 6: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables.Tests/ <changed test/fixture files>
git commit -m "feat(payables): AP requires {Vendor, Bill} — unfoldable AP lines impossible

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: Read paths fold the ledger

Switch every derived A/P figure to fold the ledger. **Pin every sign with a test** (A/P credit-normal → `open = −fold`; Vendor Credits asset → credit fold positive, NO negation). Build the pending-inclusive over-application check in from the start.

**Files:**
- Create: `Modules/Payables/Accounting101.Payables/SettlementRelief.cs`
- Modify: `VendorAccountService.cs` (the vendor-360 assembler; add `ILedgerClient` to its ctor), `BillPaymentService.cs`, `VendorAccountBuilder.cs` (`Statement`/`CreditActivity` gain a `reliefByDocument` parameter).
- Test: `Modules/Payables/Accounting101.Payables.Tests/FoldReadTests.cs` (new).

**Interfaces:**
- Consumes: `ILedgerClient.GetSubledgerAsync` (Task 1); `VendorAccountBuilder.OpenBills(bills, applied, asOf)` / `.Aging` / `.ApBalance` (unchanged — still take the `applied` dict).
- Produces: `SettlementRelief.ForSourceAsync(ILedgerClient ledger, Guid clientId, Guid sourceRef, Guid payableAccountId, CancellationToken ct, bool postedOnly)` → the document's A/P relief (sum of its entry's A/P-account lines by magnitude); reads Posted-only, the void guard immediate.

- [ ] **Step 1: Read the AR reference.** Read (on master) `Modules/Receivables/Accounting101.Receivables/SettlementRelief.cs`, `CustomerAccountService.cs`, and the relevant `PaymentService.cs` methods — they are the exact template. Port them changing: `Customer→Vendor`, `Invoice→Bill`, `Receivable→Payable`, `CustomerCredits→VendorCredits`, and — **critically** — the SIGNS per this task's sign rules.

- [ ] **Step 2: Add `SettlementRelief`.** Create `Modules/Payables/Accounting101.Payables/SettlementRelief.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>Folds one settlement document's A/P relief from its own ledger entry — what "Allocated"/"Applied"
/// meant when the module stored an Allocation[]. Sums the entry's lines on the Payable account. When
/// <paramref name="postedOnly"/> is true a not-yet-Posted entry contributes 0 (read surfaces); when false the
/// relief is immediate (the payment-void negative-credit guard needs it before approval).</summary>
internal static class SettlementRelief
{
    public static async Task<decimal> ForSourceAsync(
        ILedgerClient ledger, Guid clientId, Guid sourceRef, Guid payableAccountId, CancellationToken ct, bool postedOnly)
    {
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, sourceRef, ct);
        EntryResponse? entry = entries.FirstOrDefault(e => e.ReversalOf is null);
        if (entry is null) return 0m;
        if (postedOnly && entry.Posting != "Posted") return 0m;
        return entry.Lines.Where(l => l.AccountId == payableAccountId).Sum(l => l.Amount);
    }
}
```
(A/P relief lines are Debits; `l.Amount` is the positive magnitude, so this sums the applied total — same shape as AR's, using the Payable account.)

- [ ] **Step 3: Write the failing fold tests.** Create `FoldReadTests.cs` with, at minimum:
  - **Bill-open-balance sign test:** enter+approve a bill (100), pay 30 (approved), assert the vendor-account view / `GetBillViewAsync` reports the bill's `OpenBalance == 70`, AND that it equals `−(Bill fold for that bill)` read directly from `GetSubledgerAsync(payable, "Bill")`. This pins `open = −fold`.
  - **Vendor-credit sign test:** record a payment with an overpayment (pay 100, allocate 40 → 60 unapplied), approve it, assert the vendor credit balance reads **+60** (positive) AND that the raw Vendor Credits fold (`GetSubledgerAsync(vendorCredits, "Vendor")`) for that vendor is **already positive +60** (asset, debit-normal — NO negation). A negation here would give −60; assert it does NOT.
  - **Approval-timing test:** record a payment of 30 against the bill but do NOT approve; assert the bill still reads `OpenBalance == 100` (reads are Posted-only), then approve and assert 70.

- [ ] **Step 4: Run to verify they fail / drive the change.** Run: `dotnet test ... --filter "FullyQualifiedName~FoldRead"`. Expected: the sign/approval-timing tests fail against the current stored-allocation reads (esp. approval-timing: today it reads 70 before approval).

- [ ] **Step 5: Convert the vendor-account view assembler.** In the vendor-360 assembler (AR analog `CustomerAccountService.GetAccountAsync`), inject `ILedgerClient` if not present, and replace the stored-allocation folds:
```csharp
    // Bill open balances from the Bill-axis A/P fold. A/P is credit-normal → the debit-positive fold reads
    // the outstanding payable NEGATIVE, so open = −fold.
    IReadOnlyList<SubledgerLineResponse> apByBill =
        await ledger.GetSubledgerAsync(clientId, accounts.PayableAccountId, "Bill", asOf, ct);
    Dictionary<Guid, decimal> openByBill = apByBill.ToDictionary(l => l.DimensionValue, l => -l.Balance);
    // applied = total − open; OpenBills keeps its (total, applied) contract. A bill absent from the fold
    // (its A/P line not yet on the books) defaults to fully open.
    Dictionary<Guid, decimal> applied = bills.ToDictionary(
        b => b.Id, b => b.Total - openByBill.GetValueOrDefault(b.Id, b.Total));

    // Vendor Credits is an ASSET (debit-normal): the fold reads available credit POSITIVE — NO negation.
    decimal credit = (await ledger.GetSubledgerAsync(clientId, accounts.VendorCreditsAccountId, "Vendor", asOf, ct))
        .Where(l => l.DimensionValue == vendorId).Sum(l => l.Balance);
```
Feed `applied` into the unchanged `VendorAccountBuilder.OpenBills(bills, applied, asOf)` → `Aging`/`ApBalance`. Build a `reliefByDocument` dict (Posted-only) for non-voided payments + credit-applications via `SettlementRelief.ForSourceAsync(..., postedOnly: true)` and feed it to `Statement`/`CreditActivity` (see Step 7). (This mirrors AR's `CustomerAccountService` exactly, with Vendor names and the sign changes above.)

- [ ] **Step 6: Convert `BillPaymentService`.** Replace the stored-allocation folds:
  - `AppliedToBillAsync(clientId, vendorId, billId, ct)` (Posted-only read) → `bill.Total − open`, where `open = −(Bill fold value for billId)` from `GetSubledgerAsync(payable, "Bill", null, ct)` (absent ⇒ open = bill.Total). Used by `GetBillViewAsync`.
  - `ValidateAllocationsAsync` → keep the exact rule (`alreadyApplied + a.Amount > bill.Total` ⇒ reject) but source `alreadyApplied` from the **pending-inclusive** fold: `GetSubledgerAsync(payable, "Bill", null, ct, includePending: true)`, `alreadyApplied = bill.Total − (−foldPendingInclusive[billId] ?? bill.Total→0 applied)`. (Precisely: `open_pending = −foldPending[billId] (absent ⇒ bill.Total); alreadyApplied = bill.Total − open_pending`.) Add a test: two unapproved payments each 100 against a 100 bill → the second rejected.
  - `ListBillViewsAsync` inline `applied` dict → build from the `Bill` fold (Posted-only, same as Step 5).
  - `GetVendorCreditBalanceAsync` → the Vendor Credits `Vendor`-axis fold, **no negation** (`Sum(l => l.Balance)` filtered to the vendor). This is used both as a read AND by `VoidPaymentAsync`'s guard — a Posted-only fold is correct for it (available credit reflects posted state).
  - `VoidPaymentAsync`'s guard uses `payment.Unapplied` — after Task 7, that comes from `SettlementRelief` with `postedOnly: false` (immediate: the payment being voided may itself be pending). Keep the guard's logic; ensure `payment.Unapplied` is immediate. Leave the void reverse/withdraw branch unchanged.

- [ ] **Step 7: Give `Statement`/`CreditActivity` a relief dict.** In `VendorAccountBuilder.cs`, change `Statement` and `CreditActivity` to accept `IReadOnlyDictionary<Guid, decimal> reliefByDocument` and use it instead of `p.Allocations.Sum(...)` / `p.Unapplied` / `c.Applied`:
  - `Statement`: a payment's "Payment" column = `reliefByDocument.GetValueOrDefault(p.Id)`; a credit-application's = `reliefByDocument.GetValueOrDefault(c.Id)`.
  - `CreditActivity`: an overpayment amount = `p.Amount − reliefByDocument.GetValueOrDefault(p.Id)` (unapplied = amount − applied); a credit-application's applied = `reliefByDocument.GetValueOrDefault(c.Id)`.
  The assembler (Step 5) passes the Posted-only `reliefByDocument`. This keeps Statement/CreditActivity working after Task 7 deletes `Allocation[]` (converting them here, not letting Task 7 surprise-break them as happened in AR).

- [ ] **Step 8: Run to verify pass.** Run: `dotnet test ... --filter "FullyQualifiedName~FoldRead"` then the whole Payables suite. Expected: all PASS. Fix any sign error the tests surface — the sign tests are the guard.

- [ ] **Step 9: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables/SettlementRelief.cs \
        Modules/Payables/Accounting101.Payables/<VendorAccountService.cs> \
        Modules/Payables/Accounting101.Payables/BillPaymentService.cs \
        Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs \
        Modules/Payables/Accounting101.Payables.Tests/FoldReadTests.cs \
        <DI registration file if changed>
git commit -m "feat(payables): derive AP balances from ledger folds (sign-correct; pending-inclusive validation)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: Delete `Allocation[]` storage

Remove the second source: `Allocation[]` on the persisted bodies, and the accessors/builder that read it.

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/BillPayment.cs` (drop `Allocations` from `BillPayment` + `VendorCreditApplication`; recompute `Allocated`/`Unapplied`/`Applied` from the entry), `BillPaymentBody.cs` (drop `Allocations` from persisted bodies; add command records), `BillPaymentService.cs` (record methods accept commands; `Allocated`/`Unapplied` sourced from `SettlementRelief`), `VendorAccountBuilder.cs` (delete `AppliedByBill`).
- Test: adjust any test constructing a body/type with `Allocations`; add a "no allocation persisted" test.

**Interfaces:**
- Produces: persisted bill-payment/credit-application documents with NO allocation array; request-command records (`BillPaymentCommand`, `VendorCreditApplicationCommand`) carry allocations to the recipe.

- [ ] **Step 1: Write the failing test.** In `FoldReadTests.cs` (or new `NoAllocationStorageTests.cs`): record a payment (30 against a 100 bill), re-read the persisted payment, assert no allocation array is exposed, and assert the bill's fold still reads open 70 (reads rely only on the ledger). If `BillPayment` no longer has `Allocations`, that is a compile-time guarantee; keep the fold assertion.

- [ ] **Step 2: Run to verify it drives the change.** Run: `dotnet test ... --filter "FullyQualifiedName~NoAllocation OR FullyQualifiedName~persists_no_allocation"`. Expected: compile failure once the test references the removed member, or the old-array assertion fails.

- [ ] **Step 3: Introduce command records and strip the bodies.** In `BillPaymentBody.cs`:
```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>Stored body of a bill payment — carries no allocation array (the per-bill split lives as ledger
/// dimensions; see BillPaymentCommand for the write-path request).</summary>
public sealed record BillPaymentBody(Guid VendorId, DateOnly Date, decimal Amount, string? Method);

/// <summary>What RecordPaymentAsync accepts: BillPaymentBody plus the caller's per-bill allocations, consumed
/// into ledger dimensions at compose time and never persisted.</summary>
public sealed record BillPaymentCommand(
    Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Stored body of a vendor-credit application — carries no allocation array; see command.</summary>
public sealed record VendorCreditApplicationBody(Guid VendorId, DateOnly Date);

public sealed record VendorCreditApplicationCommand(
    Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
```
Rewire `BillPaymentService.RecordPaymentAsync`/`RecordCreditApplicationAsync` to accept the `*Command`, build the allocation-free body for the store, and pass `command.Allocations` into `ComposeBillPayment`/`ComposeVendorCreditApplication` (change those recipes' signatures to take the allocations list explicitly, OR pass the command — match how AR did it: AR's recipes took the command/allocations as a parameter). Update the endpoint (`PayablesEndpoints`) to bind the command. Update the store (`DocumentBillPaymentStore` or equivalent) to persist the allocation-free body.

- [ ] **Step 4: Recompute the accessors.** In `BillPayment.cs`, remove `Allocations` from `BillPayment` and `VendorCreditApplication`. `BillPayment.Allocated`/`Unapplied` and `VendorCreditApplication.Applied` now come from the ledger entry: since these are records without a ledger client, do NOT compute them on the type — instead have the callers that need them (the vendor-account assembler, `VoidPaymentAsync`, `GetVendorCreditBalanceAsync`) obtain the relief via `SettlementRelief.ForSourceAsync`. Specifically `VoidPaymentAsync`'s guard: replace `payment.Unapplied` with `payment.Amount − await SettlementRelief.ForSourceAsync(ledger, clientId, paymentId, posting.PayableAccountId, ct, postedOnly: false)`. Check EVERY use of `.Allocated`/`.Unapplied`/`.Applied`/`.Allocations` across the module and reroute to the fold/relief.

- [ ] **Step 5: Delete the dead builder fold.** Remove `VendorAccountBuilder.AppliedByBill`. `OpenBills`/`Aging`/`ApBalance`/`Statement`/`CreditActivity` remain (fed by Task 6). Fix the compile.

- [ ] **Step 6: Update tests** that built `Allocations` into persisted bodies/types → construct the command instead and read results via folds. Do not weaken.

- [ ] **Step 7: Run the whole Payables suite.** Expected: all PASS. (`Accounting101.Settlement.Allocation` stays — the commands use it.)

- [ ] **Step 8: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables/BillPayment.cs \
        Modules/Payables/Accounting101.Payables/BillPaymentBody.cs \
        Modules/Payables/Accounting101.Payables/BillPaymentService.cs \
        Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs \
        Modules/Payables/Accounting101.Payables/BillPosting.cs \
        <changed store/endpoint + test files>
git commit -m "refactor(payables): delete Allocation[] storage — the ledger dimension is the allocation

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 8: Proof suite + whole-solution reconciliation

Codify the §9 proof obligations and confirm the whole solution is green.

**Files:**
- Test: `Modules/Payables/Accounting101.Payables.Tests/ApLedgerFirstProofTests.cs` (new)

- [ ] **Step 1: Write the proof suite.** Create `ApLedgerFirstProofTests.cs`, one test per obligation (reuse `PayablesE2eTests` setup helpers; A/P now requires `{Vendor, Bill}`):
  1. Bill enter → the bill's `Bill`-axis fold gives open = full total (correct sign).
  2. Partial payment (approved) → the bill's fold-derived open reduces by the allocation.
  3. **Split payment across two bills → each bill's open reduces by its own allocation.**
  4. Vendor-axis fold == sum of the vendor's open bills; `dimension=Vendor` AND `dimension=Bill` reconciliation `TiesOut`.
  5. Over-application: single allocation exceeding a bill's open balance rejected; **two unapproved payments each 100 vs a 100 bill → the second rejected at record** (pending-inclusive).
  6. Raw A/P line missing the `Bill` tag → 422 (may reference `ApRequiresBillTests`).
  7. A vendor credit application relieving a bill → that bill's open reduces by the applied amount.
  8. **Void the bill's entry through the module's void surface → the bill's `Bill`-axis fold-derived open drops to full-void state (0 outstanding for that bill) in the same read.**
  9. **Vendor credit balance reads POSITIVE for an unapplied overpayment** (asset, no negation) — the sign proof.
  10. After a payment, the persisted payment document carries no allocation array.

- [ ] **Step 2: Run the proof suite.** Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~ApLedgerFirstProof"`. Expected: all PASS.

- [ ] **Step 3: Run the whole solution.** Run: `dotnet test Accounting101.slnx`. Expected: all PASS. Triage any failure: a Payables test asserting old behavior → re-point to the fold / add approval (never weaken); another module → the A/P two-dimension requirement is scoped to the A/P account only, so others should be unaffected — investigate before touching.

- [ ] **Step 4: Commit.**
```bash
git add Modules/Payables/Accounting101.Payables.Tests/ApLedgerFirstProofTests.cs \
        <any reconciled test files>
git commit -m "test(payables): AP ledger-first proof suite — single source of truth end to end

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the executor

- **Staging order is the safety net** (same as AR): recipes gain the `Bill` tag additively (Tasks 2–4) BEFORE A/P requires it (Task 5); reads fold (Task 6) BEFORE `Allocation[]` is deleted (Task 7). Never reorder.
- **⭐ Signs are the #1 risk and differ from AR.** A/P is credit-normal → `open = −fold`. Vendor Credits is a debit-normal Asset → credit fold is positive, **NO negation** (AR negated a liability). The Task-6 sign tests exist to catch a blind AR copy — do not delete or weaken them.
- **Convert `Statement`/`CreditActivity` in Task 6, not Task 7** — AR discovered late (in its delete task) that these still read allocations; do it up front here.
- **`VoidPaymentAsync`'s negative-credit guard needs immediate relief** (`postedOnly: false`) — a pending payment being voided must still see its own overpayment. Reads (`GetVendorCreditBalanceAsync`, the view) are Posted-only.
- **Global `IgnoreExtraElements` (shipped with AR) already tolerates legacy AP documents** with a stale `Allocations` element on read — deleting the field (Task 7) will not 500 on pre-existing docs. The dev smoke should confirm.
- **Dev smoke config** (Task-8 follow-through / merge gate): the dev chart must configure A/P `{Vendor, Bill}` and Vendor Credits `{Vendor}` (PUT via API; prod seeding is runtime-only, same as AR).
- Aging/statement are NOT rebuilt fold-native here; they keep passing via the re-fed `applied`/`reliefByDocument`.
