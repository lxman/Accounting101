# AR Ledger-First Core — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the ledger the single source of truth for Receivables — every A/R balance becomes a fold over Invoice/Customer-dimensioned journal lines, and the module stores no monetary amount or allocation array.

**Architecture:** Journal lines already carry an open-ended `Dimensions` (`Dictionary<string, Guid>`) that Mongo already indexes and `AggregateSubledgerAsync` already folds. This plan (1) lets a control account require a *set* of dimensions and enforces it at post; (2) makes every A/R-relieving recipe (issue, payment, write-off, credit note, credit application) tag its A/R line with `{Customer, Invoice}`, so the per-invoice split — today an opaque `Allocation[]` in the module — becomes ledger dimensions; (3) switches all read paths to fold the ledger; (4) deletes `Allocation[]`. The change is staged so the module compiles and every suite is green at each commit: recipes gain the dimension first (additive), then A/R's requirement flips on, then reads switch to folds, then the allocation storage is removed.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB, xUnit, `WebApplicationFactory<Program>` host fixtures, EphemeralMongo (`SharedMongo`).

## Global Constraints

- **Dimension keys are exactly** `"Customer"` and `"Invoice"` (string literals; the module already has `CustomerDimension = "Customer"`). A/R lines must carry both.
- **A control account's required dimensions are a set;** a line touching it must carry every dimension in the set or the post is rejected **422**, naming the missing dimension. Existing single-dimension accounts (AP `Vendor`, etc.) are unchanged.
- **`AggregateSubledgerAsync` / `GET /subledger` are unchanged** — they already take one `dimensionType` string per call and fold `balance grouped by dimension value`. A/R with a two-dimension set is queried once per axis (`?dimension=Customer`, `?dimension=Invoice`).
- **A/R (asset, debit-normal) fold semantics:** `AggregateSubledgerAsync(account=AR, dimensionType="Invoice")` returns, per invoice value, the signed (debit-positive) balance = issue debit − relief credits = **open balance**. Customer axis sums the same lines by customer.
- **Refund does not relieve A/R** (`Dr Customer Credits / Cr Cash`) — it carries no `Invoice` dimension. Only issue, payment, write-off, credit note, and credit application touch A/R.
- **Greenfield:** no historical AR data is preserved; dev/demo is reseeded. No backfill.
- **Commit trailer on every commit:** `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`. Stage explicit paths only, never `-A`. Do NOT stage `UI/Angular/src/app/core/api/environment.ts`.
- Spec: `docs/superpowers/specs/2026-07-09-ar-ledger-first-core-design.md`. Parent: `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md`.

## File structure (what each task touches)

- **Engine (Task 1):** `Backend/Accounting101.Ledger.Contracts/AccountContracts.cs` (wire), `Backend/Accounting101.Ledger.Core/Accounts/Account.cs` (domain), `Backend/Accounting101.Ledger.Mongo/Documents/AccountDocument.cs` (persistence), `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`MapAccount`, `ToAccountResponse`, `ChartFieldViolationsAsync`, `ChartViolationsAsync`, `GetSubledger`, `GetSubledgerReconciliation`).
- **Module read client (Task 2):** `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs`, `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`.
- **Recipes (Tasks 3–5):** `Modules/Receivables/Accounting101.Receivables/InvoicePosting.cs`, `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs`.
- **Enforcement flip (Task 6):** test fixture `ReceivablesHostFixture.cs` / `SetUpChartAsync` helpers, prod account seeding doc, onboarding path.
- **Read paths (Task 7):** `Modules/Receivables/Accounting101.Receivables/CustomerAccountService.cs`, `CustomerAccountBuilder.cs`, `PaymentService.cs`.
- **Storage deletion (Task 8):** `Modules/Receivables/Accounting101.Receivables/Payment.cs`, `PaymentBody.cs`, `DispositionBodies.cs`, `DocumentPaymentStore.cs`, `CustomerAccountBuilder.cs`.
- **Proof suite (Task 9):** `Modules/Receivables/Accounting101.Receivables.Tests/ArLedgerFirstProofTests.cs` (new).

---

### Task 1: Engine — control accounts require a *set* of dimensions

Give an account a set of required dimensions (canonical), accept the legacy single `RequiredDimension` on the wire for backward compatibility, enforce the whole set at post, and let the subledger endpoints accept any dimension in the set. Existing single-dimension accounts behave identically.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AccountContracts.cs`
- Modify: `Backend/Accounting101.Ledger.Core/Accounts/Account.cs`
- Modify: `Backend/Accounting101.Ledger.Mongo/Documents/AccountDocument.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`MapAccount`, `ToAccountResponse`, `ChartFieldViolationsAsync`, `ChartViolationsAsync`, `GetSubledger`, `GetSubledgerReconciliation`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/RequiredDimensionSetTests.cs` (new)

**Interfaces:**
- Consumes: `AccountRequest`, `AccountResponse`, `Account`, `AccountDocument`, the two chart-violation methods, and the two subledger handlers as they exist today (single `string? RequiredDimension`).
- Produces: `Account.RequiredDimensions` (`IReadOnlyCollection<string>`, canonical, never null — empty means unconstrained); `AccountRequest.RequiredDimensions` (`IReadOnlyList<string>?`, optional new input) alongside the retained `RequiredDimension`; `AccountResponse.RequiredDimensions` alongside the retained `RequiredDimension` (first-or-null). Enforcement rejects a line missing ANY required dimension. Consumed by Tasks 3–9.

- [ ] **Step 1: Write the failing test.** Create `Backend/Accounting101.Ledger.Api.Tests/RequiredDimensionSetTests.cs`. Mirror the existing account/post fixture pattern used by `ModulePostingTests.cs`/`PolicyTests.cs` in that project (an `ApiFixture` with `SeedClientAsync`, a Controller `Http`, `PutAsJsonAsync` for accounts, `PostAsJsonAsync` for entries). Use the same `PostEntryRequest`/`PostLineRequest`/`AccountRequest` types.

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class RequiredDimensionSetTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static AccountRequest Asset(string number, string name, IReadOnlyList<string>? dims = null, string? legacy = null) =>
        new() { Number = number, Name = name, Type = "Asset", RequiredDimensions = dims, RequiredDimension = legacy };

    [Fact]
    public async Task Account_with_two_required_dims_rejects_a_line_missing_either()
    {
        SeededClient c = await fixture.SeedClientAsync("ReqDimSetTwo");
        Guid ar = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            Asset("1200", "AR", dims: ["Customer", "Invoice"]))).EnsureSuccessStatusCode();
        Guid rev = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{rev}",
            new AccountRequest { Number = "4000", Name = "Rev", Type = "Revenue" })).EnsureSuccessStatusCode();

        Guid cust = Guid.NewGuid();
        // Missing "Invoice" → 422 naming it.
        PostEntryRequest missingInvoice = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m, new Dictionary<string, Guid> { ["Customer"] = cust }),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        HttpResponseMessage bad = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", missingInvoice);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("Invoice", await bad.Content.ReadAsStringAsync());

        // Both present → OK.
        Guid inv = Guid.NewGuid();
        PostEntryRequest ok = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m, new Dictionary<string, Guid> { ["Customer"] = cust, ["Invoice"] = inv }),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        HttpResponseMessage good = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", ok);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task Legacy_single_RequiredDimension_still_enforced_unchanged()
    {
        SeededClient c = await fixture.SeedClientAsync("ReqDimLegacy");
        Guid ar = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            Asset("1200", "AR", legacy: "Customer"))).EnsureSuccessStatusCode();
        Guid rev = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{rev}",
            new AccountRequest { Number = "4000", Name = "Rev", Type = "Revenue" })).EnsureSuccessStatusCode();

        PostEntryRequest missingCustomer = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        HttpResponseMessage bad = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", missingCustomer);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        // Response echoes the dimension in the canonical set.
        AccountResponse acct = (await c.Http.GetFromJsonAsync<AccountResponse>($"/clients/{c.ClientId}/accounts/{ar}"))!;
        Assert.Contains("Customer", acct.RequiredDimensions);
        Assert.Equal("Customer", acct.RequiredDimension);
    }

    [Fact]
    public async Task Subledger_endpoint_accepts_any_dimension_in_the_set()
    {
        SeededClient c = await fixture.SeedClientAsync("ReqDimSubledger");
        Guid ar = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            Asset("1200", "AR", dims: ["Customer", "Invoice"]))).EnsureSuccessStatusCode();
        Guid rev = Guid.NewGuid();
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{rev}",
            new AccountRequest { Number = "4000", Name = "Rev", Type = "Revenue" })).EnsureSuccessStatusCode();
        Guid cust = Guid.NewGuid(); Guid inv = Guid.NewGuid();
        PostEntryRequest e = new(null, new DateOnly(2026, 6, 26), "R", "m",
        [
            new PostLineRequest(ar, "Debit", 100m, new Dictionary<string, Guid> { ["Customer"] = cust, ["Invoice"] = inv }),
            new PostLineRequest(rev, "Credit", 100m),
        ]);
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", e)).EnsureSuccessStatusCode();

        // Both axes resolve for the same control account.
        HttpResponseMessage byCustomer = await c.Http.GetAsync($"/clients/{c.ClientId}/subledger?account={ar}&dimension=Customer");
        HttpResponseMessage byInvoice = await c.Http.GetAsync($"/clients/{c.ClientId}/subledger?account={ar}&dimension=Invoice");
        Assert.Equal(HttpStatusCode.OK, byCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byInvoice.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~RequiredDimensionSet"`. Expected: FAILS to compile (`AccountRequest.RequiredDimensions`, `AccountResponse.RequiredDimensions` do not exist yet).

- [ ] **Step 3: Add the canonical set to the domain.** In `Backend/Accounting101.Ledger.Core/Accounts/Account.cs`, replace the `RequiredDimension` property with a canonical set plus a legacy accessor:

```csharp
    /// <summary>Dimension types every posting line touching this account MUST carry. Empty = unconstrained.
    /// A control account (e.g. A/R) may require several (Customer AND Invoice).</summary>
    public IReadOnlyCollection<string> RequiredDimensions { get; init; } = [];

    /// <summary>Legacy single-dimension accessor: the first required dimension, or null. Retained for callers/
    /// responses that predate the set. Prefer <see cref="RequiredDimensions"/>.</summary>
    public string? RequiredDimension => RequiredDimensions.Count == 0 ? null : RequiredDimensions.First();
```

- [ ] **Step 4: Persist the set.** In `Backend/Accounting101.Ledger.Mongo/Documents/AccountDocument.cs`, replace the `RequiredDimension` field with a list and map it (guard null from any older doc):

```csharp
    public List<string> RequiredDimensions { get; set; } = [];
```
In `FromDomain`, set `RequiredDimensions = account.RequiredDimensions.ToList()`. In `ToDomain`, set `RequiredDimensions = RequiredDimensions ?? []` (as the domain `IReadOnlyCollection<string>`). Remove the old `RequiredDimension` field and its two mappings.

- [ ] **Step 5: Extend the wire contracts.** In `Backend/Accounting101.Ledger.Contracts/AccountContracts.cs`, add the new input/output while keeping the legacy field:

```csharp
public sealed record AccountRequest
{
    public required string Number { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public Guid? ParentId { get; init; }
    public bool Postable { get; init; } = true;
    public string? RequiredDimension { get; init; }                 // legacy single (still honored)
    public IReadOnlyList<string>? RequiredDimensions { get; init; }  // new: full set (wins if present)
    public string? CashFlowActivity { get; init; }
    public bool IsRetainedEarnings { get; init; }
    public bool Active { get; init; } = true;
}

public sealed record AccountResponse(
    Guid Id, string Number, string Name, string Type, Guid? ParentId, bool Postable,
    string? RequiredDimension, IReadOnlyList<string> RequiredDimensions,
    string? CashFlowActivity, bool IsRetainedEarnings, bool Active, string NormalSide, bool IsTemporary);
```

- [ ] **Step 6: Map both directions.** In `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs`:
  - In `MapAccount` (~line 794-809), replace the `RequiredDimension = request.RequiredDimension` assignment with the canonical set computed from either input:
    ```csharp
        RequiredDimensions = request.RequiredDimensions is { Count: > 0 } set
            ? set.Distinct().ToArray()
            : request.RequiredDimension is { } single ? [single] : [],
    ```
  - In `ToAccountResponse` (~line 789), emit both: pass `a.RequiredDimension` (the legacy accessor) for the existing positional arg and add `a.RequiredDimensions.ToArray()` (or the collection) for the new one, matching the new record shape.

- [ ] **Step 7: Enforce the whole set at post.** In the same file, change both chart-violation checks from a single dimension to iterating the set:
  - `ChartFieldViolationsAsync` (~line 1096-1097): replace
    ```csharp
    if (account.RequiredDimension is { } dimension && !line.Dimensions.ContainsKey(dimension))
        lineErrors.Add($"Account {account.Number} \"{account.Name}\" requires a {dimension} on the posting line.");
    ```
    with
    ```csharp
    foreach (string dimension in account.RequiredDimensions)
        if (!line.Dimensions.ContainsKey(dimension))
            lineErrors.Add($"Account {account.Number} \"{account.Name}\" requires a {dimension} on the posting line.");
    ```
  - `ChartViolationsAsync` (~line 1057-1058): apply the identical loop transformation to its single-dimension check (it adds to a flat `List<string>` — keep that shape, just loop the set).

- [ ] **Step 8: Adapt the subledger reads to set membership.** In `GetSubledger` (~line 523/526) and `GetSubledgerReconciliation` (~line 558/561): replace the checks that require `RequiredDimension` non-empty and `string.Equals(RequiredDimension, dimension)` with set-based equivalents — the account must have at least one required dimension and the requested `dimension` query param must be a member:
  ```csharp
  if (namedAccount.RequiredDimensions.Count == 0) return Results.BadRequest(...existing message...);
  if (!namedAccount.RequiredDimensions.Contains(dimension)) return Results.BadRequest(...existing message...);
  ```
  Keep the existing error messages/response shapes; only the predicate changes.

- [ ] **Step 9: Run to verify pass.** Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~RequiredDimensionSet"`. Expected: all three PASS.

- [ ] **Step 10: Run the whole Ledger.Api.Tests project (backward-compat guard).** Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`. Expected: all PASS — existing single-dimension accounts (posted via legacy `RequiredDimension`) behave identically. Fix any mapping regression before committing.

- [ ] **Step 11: Commit.**
```bash
git add Backend/Accounting101.Ledger.Contracts/AccountContracts.cs \
        Backend/Accounting101.Ledger.Core/Accounts/Account.cs \
        Backend/Accounting101.Ledger.Mongo/Documents/AccountDocument.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/RequiredDimensionSetTests.cs
git commit -m "feat(ledger): control accounts require a set of dimensions

RequiredDimension (single) becomes a canonical RequiredDimensions set on the
account; the wire keeps the legacy single field as an alias. Post-time
enforcement and the subledger endpoints iterate the set. Existing
single-dimension accounts are unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Receivables ledger client — add a subledger read-fold method

The module's `ILedgerClient` has no way to read a per-dimension fold (only Post/Approve/Reverse/Void/Validate/GetEntriesBySourceRef). Add one so read paths (Task 7) can fold the ledger.

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/SubledgerReadTests.cs` (new)

**Interfaces:**
- Consumes: the engine `GET /clients/{clientId}/subledger?account=&dimension=&asOf=` returning `SubledgerResponse(string Dimension, DateOnly? AsOf, IReadOnlyList<SubledgerLineResponse> Lines)`, `SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance)` (both in `Accounting101.Ledger.Contracts`).
- Produces: `ILedgerClient.GetSubledgerAsync(Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken) → Task<IReadOnlyList<SubledgerLineResponse>>`. Consumed by Task 7.

- [ ] **Step 1: Write the failing test.** Create `Modules/Receivables/Accounting101.Receivables.Tests/SubledgerReadTests.cs`. Use `ReceivablesHostFixture` (the module's host fixture) exactly as `ReceivablesIssueTests` does. Issue an invoice through the module (which posts a Customer-tagged AR line today — that is enough to prove the read), then resolve the module's `ILedgerClient` from the host services and assert the fold returns the AR balance for that customer:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.Receivables;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Receivables.Tests;

public sealed class SubledgerReadTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    [Fact]
    public async Task GetSubledgerAsync_returns_the_AR_fold_for_a_customer()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        // Mirror ReceivablesIssueTests' issue flow inline: PUT AR/Rev/Tax accounts (AR RequiredDimension="Customer"),
        // POST /customers -> customerId, POST /invoices (draft) then POST /invoices/{id}/issue -> the invoice.
        // Capture customerId and the invoice total from those responses. (Non-SoD SeedClientAsync => entry Posts directly.)
        Guid customerId = /* from POST /customers */;
        decimal total = /* the issued invoice's total */;

        ILedgerClient ledger = fixture.Services.GetRequiredService<ILedgerClient>();
        IReadOnlyList<SubledgerLineResponse> fold =
            await ledger.GetSubledgerAsync(clientId, fixture.ReceivableAccountId, "Customer", null, default);

        SubledgerLineResponse line = Assert.Single(fold, l => l.DimensionValue == customerId);
        Assert.Equal(total, line.Balance);
    }
}
```
If resolving `ILedgerClient` from `fixture.Services` is awkward (the module client is scoped/keyed), instead call the engine endpoint the client wraps and assert the client returns the same — but the DI resolution mirrors how other module tests reach module services. Keep the assertion: fold balance for the issued invoice's customer equals the invoice total.

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~SubledgerRead"`. Expected: FAILS to compile (`GetSubledgerAsync` undefined).

- [ ] **Step 3: Add the interface method.** In `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs`, add:

```csharp
    /// <summary>Read a per-dimension control-account fold: the signed (debit-positive) balance of
    /// <paramref name="account"/> grouped by the value of dimension <paramref name="dimension"/>
    /// (e.g. "Customer" or "Invoice"). This is how ledger-first read paths derive balances.</summary>
    Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default);
```
Add `using Accounting101.Ledger.Contracts;` if not present.

- [ ] **Step 4: Implement it.** In `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`, implement the method mirroring the existing GET methods (it attaches the same auth the other reads use; the subledger read needs no module credential — it's a plain member read like `GetEntriesBySourceRefAsync`):

```csharp
    public async Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default)
    {
        string url = $"clients/{clientId}/subledger?account={account}&dimension={Uri.EscapeDataString(dimension)}";
        if (asOf is { } d) url += $"&asOf={d:yyyy-MM-dd}";
        HttpResponseMessage response = await http.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken); // reuse the file's existing relay helper
        SubledgerResponse body = (await response.Content.ReadFromJsonAsync<SubledgerResponse>(cancellationToken))!;
        return body.Lines;
    }
```
Match the file's existing base-address/relative-URL convention (leading slash or not) and its established error-relay helper. Add any missing `using`.

- [ ] **Step 5: Run to verify pass.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~SubledgerRead"`. Expected: PASS.

- [ ] **Step 6: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs \
        Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/SubledgerReadTests.cs
git commit -m "feat(receivables): ledger client can read a per-dimension subledger fold

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Invoice issue recipe tags the Invoice dimension (additive)

Add `Invoice=invoice.Id` to the A/R debit line. A/R still requires only `Customer` at this point (flipped in Task 6), so the extra tag is additive and every existing test stays green.

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/InvoicePosting.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/InvoiceDimensionTests.cs` (new)

**Interfaces:**
- Consumes: `InvoicePosting.Compose(Invoice invoice, InvoicePostingAccounts accounts)`; `PostLineRequest(AccountId, Direction, Amount, Dimensions)`.
- Produces: issued-invoice AR line carrying `{Customer=invoice.CustomerId, Invoice=invoice.Id}`; `InvoicePosting.InvoiceDimension = "Invoice"`.

- [ ] **Step 1: Write the failing test.** Create `Modules/Receivables/Accounting101.Receivables.Tests/InvoiceDimensionTests.cs`. Mirror `ReceivablesIssueTests` setup (SoD client, `SetUpChartAsync`, create customer, draft + issue invoice, `GET /entries?sourceRef={invoiceId}`, approve if pending). Assert the AR line carries BOTH dimensions and the Invoice fold equals the total:

```csharp
[Fact]
public async Task Issued_invoice_AR_line_carries_Customer_and_Invoice_dimensions()
{
    // ... seed SoD client, SetUpChartAsync, create customer, draft+issue invoice of total T ...
    // read the spawned entry via GET /entries?sourceRef={invoiceId}; approve under SoD; re-read Posted entry
    EntryLineResponse ar = /* the line whose AccountId == fixture.ReceivableAccountId */;
    Assert.Equal(customerId, ar.Dimensions["Customer"]);
    Assert.Equal(invoiceId, ar.Dimensions["Invoice"]);

    // Invoice fold ties out to the invoice total.
    SubledgerReconciliationResponse recon = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
        $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Invoice"))!;
    Assert.True(recon.TiesOut);
}
```
(Use the exact line/entry response type names from the project — the same ones `ReceivablesIssueTests` reads, e.g. `EntryResponse`/its `Lines` with a `Dimensions` dictionary.)

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~InvoiceDimension"`. Expected: FAILS (`ar.Dimensions["Invoice"]` throws / key absent — the recipe tags only Customer today).

- [ ] **Step 3: Tag the Invoice dimension.** In `Modules/Receivables/Accounting101.Receivables/InvoicePosting.cs`, add the const beside `CustomerDimension`:
```csharp
    private const string InvoiceDimension = "Invoice";
```
and change the A/R debit line's `Dimensions` dictionary to include the invoice id:
```csharp
    new(accounts.ReceivableAccountId, "Debit", invoice.Total,
        Dimensions: new Dictionary<string, Guid>
        {
            [CustomerDimension] = invoice.CustomerId,
            [InvoiceDimension] = invoice.Id,
        }),
```
Leave revenue/tax lines untagged.

- [ ] **Step 4: Run to verify pass.** Run the same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Run the whole Receivables suite (additive-change guard).** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`. Expected: all PASS (the new tag is additive; A/R still requires only Customer).

- [ ] **Step 6: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables/InvoicePosting.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/InvoiceDimensionTests.cs
git commit -m "feat(receivables): invoice issue tags the AR line with Invoice dimension

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Payment recipe emits one dimensioned A/R line per allocation

Replace the single aggregate `Cr A/R` line with one `Cr A/R {Customer, Invoice=TargetId}` line per allocation. `Payment.Allocations` is still stored (deleted in Task 8); the read paths still fold it (until Task 7). A/R still requires only Customer (until Task 6), so the new tag is compatible.

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs` (`ComposePayment`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/PaymentDimensionTests.cs` (new); update any existing payment test that asserts a single aggregate AR line.

**Interfaces:**
- Consumes: `PaymentPosting.ComposePayment(Guid paymentId, PaymentBody body, PaymentPostingAccounts accounts)`; `body.Allocations` (`IReadOnlyList<Allocation>` of `Allocation(Guid TargetId, decimal Amount)`); `InvoicePosting.InvoiceDimension`/local const.
- Produces: a payment entry with N `Cr A/R` lines, line i = `{Customer=body.CustomerId, Invoice=allocations[i].TargetId}` amount `allocations[i].Amount`, plus the unchanged `Cr Customer Credits {Customer}` remainder.

- [ ] **Step 1: Write the failing test.** Create `Modules/Receivables/Accounting101.Receivables.Tests/PaymentDimensionTests.cs`. Seed a client, issue two invoices (A total 100, B total 100) for one customer, record ONE payment of 150 split 100→A, 50→B, then read the payment entry via `GET /entries?sourceRef={paymentId}` and assert two AR credit lines, each Invoice-tagged to its target with its amount; and assert each invoice's Invoice fold reduces accordingly:

```csharp
[Fact]
public async Task Split_payment_emits_one_Invoice_tagged_AR_line_per_allocation()
{
    // seed, issue invoice A (100) and B (100) for customer C, approve both under SoD or non-SoD client
    // record payment 150: allocations [ (A,100), (B,50) ]
    // GET /entries?sourceRef={paymentId} -> the Posted payment entry
    var arLines = entry.Lines.Where(l => l.AccountId == fixture.ReceivableAccountId).ToList();
    Assert.Equal(2, arLines.Count);
    Assert.Contains(arLines, l => l.Dimensions["Invoice"] == invoiceA && l.Amount == 100m && l.Dimensions["Customer"] == customerC);
    Assert.Contains(arLines, l => l.Dimensions["Invoice"] == invoiceB && l.Amount == 50m);

    // Folds: A fully relieved (open 0), B open 50.
    IReadOnlyList<SubledgerLineResponse> byInvoice = /* GET /subledger?account=AR&dimension=Invoice */;
    Assert.Equal(0m, byInvoice.Single(l => l.DimensionValue == invoiceA).Balance);
    Assert.Equal(50m, byInvoice.Single(l => l.DimensionValue == invoiceB).Balance);
}
```
Use the project's real entry-read types. If SoD is on, approve the payment entry before reading Posted; `SeedClientAsync` (single Controller) avoids SoD and is simpler for this test.

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~PaymentDimension"`. Expected: FAILS (today one aggregate AR line, no Invoice tag).

- [ ] **Step 3: Rewrite `ComposePayment`.** In `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs`, replace the aggregate AR line with a per-allocation loop. Add an `InvoiceDimension = "Invoice"` const if not already present in this file:
```csharp
    public static PostEntryRequest ComposePayment(Guid paymentId, PaymentBody body, PaymentPostingAccounts accounts)
    {
        decimal allocated = body.Allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;

        List<PostLineRequest> lines = [new(accounts.CashAccountId, "Debit", body.Amount)];
        foreach (Allocation a in body.Allocations)
            lines.Add(new(accounts.ReceivableAccountId, "Credit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [CustomerDimension] = body.CustomerId,
                    [InvoiceDimension] = a.TargetId,
                }));
        if (remainder != 0m)
            lines.Add(new(accounts.CustomerCreditsAccountId, "Credit", remainder,
                Dimensions: new Dictionary<string, Guid> { [CustomerDimension] = body.CustomerId }));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(PaymentSourceType, paymentId), EffectiveDate: body.Date,
            Reference: null, Memo: null, Lines: lines, SourceRef: paymentId, SourceType: PaymentSourceType);
    }
```
(Note: a zero-amount allocation should not occur — allocations are validated non-trivial upstream — but if the existing validation permits `a.Amount == 0`, skip it in the loop to avoid a zero line. Match existing behavior.)

- [ ] **Step 4: Update any broken existing payment test.** Run the whole Receivables suite (Step 5 command). Any existing test asserting a single aggregate `Cr A/R` line of `allocated` now sees N lines — update those assertions to the per-allocation shape (do not weaken them: assert the per-line amounts sum to the same allocated total and each carries its Invoice tag). Reconciliation/`TiesOut` assertions on the Customer axis remain valid (the Customer tag is still present on every AR line).

- [ ] **Step 5: Run the whole Receivables suite.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`. Expected: all PASS after the assertion updates.

- [ ] **Step 6: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/PaymentDimensionTests.cs \
        <any updated existing payment test files>
git commit -m "feat(receivables): payment emits one Invoice-dimensioned AR line per allocation

The per-invoice split moves from the module's Allocation[] onto dimensioned
ledger lines. Allocation[] is still stored for now (removed later).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Disposition recipes emit dimensioned A/R lines (write-off, credit note, credit application)

Apply the same per-allocation, Invoice-tagged change to the three A/R-relieving dispositions. Refund is unchanged (relieves no A/R). `Allocation[]` is still stored (Task 8 removes it). A/R still requires only Customer (Task 6 flips it).

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs` (`ComposeWriteOff`, `ComposeCreditNote`, `ComposeCreditApplication`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/DispositionDimensionTests.cs` (new); update existing disposition tests asserting aggregate AR lines.

**Interfaces:**
- Consumes: `ComposeWriteOff(Guid writeOffId, WriteOffBody body, PaymentPostingAccounts accounts)`, `ComposeCreditNote(Guid creditNoteId, CreditNoteBody body, PaymentPostingAccounts accounts)`, `ComposeCreditApplication(Guid id, CreditApplicationBody body, PaymentPostingAccounts accounts)`; each body's `Allocations`.
- Produces: each disposition entry's `Cr A/R` (or the A/R-relieving leg) split one line per allocation, `{Customer, Invoice=TargetId}`.

- [ ] **Step 1: Write the failing test.** Create `Modules/Receivables/Accounting101.Receivables.Tests/DispositionDimensionTests.cs`. For a write-off (the clearest reliever: `Dr Bad Debt / Cr A/R`): issue an invoice (100), write off 40 allocated to it, read the write-off entry, assert one `Cr A/R {Customer, Invoice}` line of 40, and assert the invoice's Invoice fold = 60:

```csharp
[Fact]
public async Task Writeoff_relieves_the_invoice_via_an_Invoice_tagged_AR_line()
{
    // seed, issue invoice (100) for customer C, approve
    // POST the write-off of 40 allocated to the invoice (existing write-off endpoint)
    // GET /entries?sourceRef={writeOffId}
    var ar = entry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId);
    Assert.Equal(40m, ar.Amount);
    Assert.Equal("Credit", ar.Direction);
    Assert.Equal(invoiceId, ar.Dimensions["Invoice"]);
    Assert.Equal(customerId, ar.Dimensions["Customer"]);

    IReadOnlyList<SubledgerLineResponse> byInvoice = /* GET /subledger?account=AR&dimension=Invoice */;
    Assert.Equal(60m, byInvoice.Single(l => l.DimensionValue == invoiceId).Balance);
}
```
Add analogous cases for credit note and credit application if the fixtures make them cheap; at minimum cover write-off + credit application (the two distinct relieving legs).

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~DispositionDimension"`. Expected: FAILS (aggregate AR line, no Invoice tag).

- [ ] **Step 3: Rewrite the three composers.** In `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs`, change each of `ComposeWriteOff`, `ComposeCreditNote`, `ComposeCreditApplication` to emit one Invoice-tagged A/R line per allocation, exactly like `ComposePayment` (Task 4). For each, the A/R leg (`Cr A/R` for write-off/credit note; the A/R relief for credit application) becomes a loop over `body.Allocations` producing lines `{Customer=body.CustomerId, Invoice=a.TargetId}` amount `a.Amount`; the counter-leg (Dr Bad Debt / Dr Sales Returns / Dr Customer Credits) stays a single line for the total. Keep `SourceType`, `Id` (`EntryIdentity.ForSource`), effective date, and `ComposeRefund` untouched.

Example — `ComposeWriteOff`:
```csharp
    public static PostEntryRequest ComposeWriteOff(Guid writeOffId, WriteOffBody body, PaymentPostingAccounts accounts)
    {
        decimal total = body.Allocations.Sum(a => a.Amount);
        List<PostLineRequest> lines = [new(accounts.BadDebtExpenseAccountId, "Debit", total)];
        foreach (Allocation a in body.Allocations)
            lines.Add(new(accounts.ReceivableAccountId, "Credit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [CustomerDimension] = body.CustomerId,
                    [InvoiceDimension] = a.TargetId,
                }));
        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(WriteOffSourceType, writeOffId), EffectiveDate: body.Date,
            Reference: null, Memo: null, Lines: lines, SourceRef: writeOffId, SourceType: WriteOffSourceType);
    }
```
Apply the same transformation to `ComposeCreditNote` (counter-leg `Dr Sales Returns`, `SalesReturnsAccountId`) and `ComposeCreditApplication` (counter-leg `Dr Customer Credits`, `CustomerCreditsAccountId`). Use the exact account-property and source-type names already in the file.

- [ ] **Step 4: Update broken existing disposition tests.** As in Task 4 Step 4 — any existing test asserting a single aggregate AR line updates to the per-allocation shape (sum preserved, Invoice tag asserted). Do not weaken assertions.

- [ ] **Step 5: Run the whole Receivables suite.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`. Expected: all PASS.

- [ ] **Step 6: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/DispositionDimensionTests.cs \
        <any updated existing disposition test files>
git commit -m "feat(receivables): write-off/credit-note/credit-application relieve invoices via dimensioned AR lines

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: Flip A/R to require {Customer, Invoice}

Now that every A/R-relieving recipe tags the Invoice dimension, make the A/R account require both — so an untagged (unfoldable) A/R line becomes structurally impossible. Handle any A/R opening-balance seeded at onboarding.

**Files:**
- Modify: the test account-setup helpers that configure A/R (`SetUpChartAsync` in `ReceivablesIssueTests.cs` and any sibling test that PUTs the A/R account) — change `RequiredDimension = "Customer"` to `RequiredDimensions = ["Customer", "Invoice"]`.
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesHostFixture.cs` only if it sets A/R's required dimension centrally (it may not — accounts are PUT per-test).
- Modify: production A/R account seeding (if any exists in a deployment/config doc or a seeder) — set the two-dimension requirement. If A/R is provisioned only via the runtime `PUT /accounts` from `.localdev` config, note that the smoke step (Task 9) must PUT A/R with both dimensions.
- Investigate + handle: the onboarding / `OpenAsync` opening-balance path — if it posts any A/R line, that line must carry an `Invoice` dimension (model the opening balance as an opening-balance invoice id) or onboarding will now 422.
- Test: extend `Modules/Receivables/Accounting101.Receivables.Tests/InvoiceDimensionTests.cs` (or a new `ArRequiresInvoiceTests.cs`) with an enforcement-proof test.

**Interfaces:**
- Consumes: `AccountRequest.RequiredDimensions` (Task 1); all recipes now emit the Invoice tag (Tasks 3–5).
- Produces: A/R configured `{Customer, Invoice}`; a proof that a raw post of an A/R line missing Invoice is rejected 422.

- [ ] **Step 1: Write the failing enforcement-proof test.** Add to the Receivables tests a case that PUTs A/R with `RequiredDimensions = ["Customer", "Invoice"]` and posts a hand-built entry with an A/R line tagged Customer-only (bypassing the recipe), asserting 422 naming Invoice:

```csharp
[Fact]
public async Task Raw_AR_line_without_Invoice_dimension_is_rejected()
{
    (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
    // PUT AR with RequiredDimensions ["Customer","Invoice"], PUT a revenue account
    PostEntryRequest e = new(null, new DateOnly(2026, 6, 26), "R", "m",
    [
        new PostLineRequest(fixture.ReceivableAccountId, "Debit", 100m,
            new Dictionary<string, Guid> { ["Customer"] = Guid.NewGuid() }),   // no Invoice
        new PostLineRequest(fixture.RevenueAccountId, "Credit", 100m),
    ]);
    HttpResponseMessage r = await http.PostAsJsonAsync($"/clients/{clientId}/entries", e);
    Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, r.StatusCode);
    Assert.Contains("Invoice", await r.Content.ReadAsStringAsync());
}
```

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~ArRequiresInvoice OR FullyQualifiedName~Raw_AR_line"`. Expected: FAILS (A/R still requires only Customer, so the Customer-only line posts 200).

- [ ] **Step 3: Flip the A/R requirement in every A/R account setup.** Change each place a test/fixture/seed PUTs the A/R account from `RequiredDimension = "Customer"` to `RequiredDimensions = ["Customer", "Invoice"]`. Search the Receivables test project for `ReceivableAccountId` PUTs and `RequiredDimension = "Customer"` and update all of them.

- [ ] **Step 4: Investigate + handle onboarding opening balances.** Search the onboarding/`OpenAsync` path (engine `Onboard` handler and any Receivables seed) for a posted A/R line. If none posts an A/R opening balance, note that in the report and no change is needed. If one does, give it an `Invoice` dimension (an opening-balance pseudo-invoice id) so it satisfies the requirement; add a focused test that onboarding with an A/R opening balance still succeeds.

- [ ] **Step 5: Run the whole Receivables suite.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`. Expected: all PASS — every recipe now supplies the Invoice tag, and the enforcement-proof test passes. Any failure here means a recipe path still emits an untagged A/R line — fix the recipe, not the requirement.

- [ ] **Step 6: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables.Tests/ <changed test/fixture files> \
        <any onboarding/seed files changed>
git commit -m "feat(receivables): AR requires {Customer, Invoice} — unfoldable AR lines impossible

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: Read paths fold the ledger

Switch every derived AR figure to fold the ledger instead of the module's `Allocation[]`: applied-per-invoice (feeding the unchanged OpenInvoices/Aging/Statement), customer AR balance, over-application validation, and customer-credit balance. `Allocation[]` is still stored but no longer read (removed in Task 8).

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/CustomerAccountService.cs` (`GetAccountAsync` — source `applied` from the fold)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` (`ValidateAllocationsAsync`, `AppliedToInvoiceAsync`, `ListInvoiceViewsAsync`, `GetInvoiceViewAsync`, `GetCustomerCreditBalanceAsync` — fold the ledger)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/FoldReadTests.cs` (new)

**Interfaces:**
- Consumes: `ILedgerClient.GetSubledgerAsync` (Task 2); `CustomerAccountBuilder.OpenInvoices(invoices, applied, asOf)`, `.Aging`, `.ArBalance`, `.Statement` (unchanged — still take the `applied` dictionary); invoice metadata `Total` per invoice.
- Produces: all AR balances derived from ledger folds. `applied(invoiceId) = invoice.Total − openFold(invoiceId)`, where `openFold` is the Invoice-axis AR fold; customer credit balance = the Customer Credits account's Customer-axis fold.

- [ ] **Step 1: Write the failing test.** Create `Modules/Receivables/Accounting101.Receivables.Tests/FoldReadTests.cs`. Prove the derived reads follow the ledger even when they would disagree with module allocations — the cleanest way is to relieve an invoice via a path and confirm the SERVICE read matches the fold. Concretely: issue invoice (100), pay 30 against it, then assert `CustomerAccountService.GetAccountAsync` reports open balance 70 for that invoice AND `PaymentService.GetCustomerCreditBalanceAsync` matches the Customer Credits fold. Resolve the services from `fixture.Services`:

```csharp
[Fact]
public async Task Customer_account_open_balance_follows_the_ledger_fold()
{
    (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
    // SetUpChart (AR requires {Customer,Invoice}), create customer C, issue invoice (100), pay 30 -> invoice
    CustomerAccountService svc = fixture.Services.GetRequiredService<CustomerAccountService>();
    CustomerAccountView view = (await svc.GetAccountAsync(clientId, customerC, asOf, default))!;
    OpenInvoiceLine line = view.OpenInvoices.Single(l => l.InvoiceId == invoiceId);
    Assert.Equal(70m, line.OpenBalance);

    ILedgerClient ledger = fixture.Services.GetRequiredService<ILedgerClient>();
    decimal invoiceFold = (await ledger.GetSubledgerAsync(clientId, fixture.ReceivableAccountId, "Invoice", null, default))
        .Single(l => l.DimensionValue == invoiceId).Balance;
    Assert.Equal(invoiceFold, line.OpenBalance);
}
```
(Use the real view/line type names — `CustomerAccountView`, `OpenInvoiceLine` — as they exist.)

- [ ] **Step 2: Run to verify it fails or is coincidentally green.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~FoldRead"`. Expected: with reads still folding `Allocation[]`, the numbers may coincidentally match (both sources agree right now). That is acceptable for a value assertion, but to make the test *drive* the change, additionally assert the service does NOT depend on `Allocation[]`: the definitive RED comes after Task 8 deletes `Allocation[]`. To get a real RED now, instead assert via a scenario only the fold can satisfy — e.g. relieve the invoice through a raw module-credentialed reversal that does not update `Allocation[]`, then assert the service open balance reflects it. If that is impractical in this task, keep the value assertions and rely on Task 8's deletion to prove the reads no longer touch `Allocation[]`; note this explicitly in the report.

- [ ] **Step 3: Source `applied` from the fold in `CustomerAccountService.GetAccountAsync`.** Inject `ILedgerClient` into `CustomerAccountService` (add constructor param; register in DI if the service isn't already resolving it). Replace the `AppliedByInvoice(...)` call with a fold-derived dictionary:
```csharp
    IReadOnlyList<SubledgerLineResponse> arByInvoice =
        await ledger.GetSubledgerAsync(clientId, accounts.ReceivableAccountId, "Invoice", asOf, ct);
    Dictionary<Guid, decimal> openByInvoice = arByInvoice.ToDictionary(l => l.DimensionValue, l => l.Balance);
    // applied = total - open; OpenInvoices keeps its existing (total, applied) contract.
    Dictionary<Guid, decimal> applied = invoices.ToDictionary(
        i => i.Id, i => i.Total - openByInvoice.GetValueOrDefault(i.Id, i.Total));
```
Feed `applied` into the existing `OpenInvoices(invoices, applied, asOf)`; `ArBalance`/`Aging`/`Statement` are unchanged. Replace the customer-credit computation with the Customer Credits fold on the Customer axis:
```csharp
    decimal credit = (await ledger.GetSubledgerAsync(clientId, accounts.CustomerCreditsAccountId, "Customer", asOf, ct))
        .Where(l => l.DimensionValue == customerId).Sum(l => -l.Balance); // credits are negative on a liability's debit-positive fold; normalize to a positive credit balance
```
(Confirm the sign against the Customer Credits account's normal side; the fold is debit-positive, so a customer-credit liability balance reads negative — negate to present a positive available credit. Add a test asserting the sign is right.)

- [ ] **Step 4: Fold the ledger in `PaymentService`.** Inject/confirm `ILedgerClient ledger` (already a ctor param). Replace the three duplicated module-allocation folds:
  - `AppliedToInvoiceAsync(clientId, customerId, invoiceId, ct)` → `applied = invoice.Total − openFold(invoiceId)`, where `openFold` is `GetSubledgerAsync(clientId, accounts.ReceivableAccountId, "Invoice", null, ct)` filtered to `invoiceId` (0 if absent).
  - `ValidateAllocationsAsync` → keep the same rule (`alreadyApplied + a.Amount > invoice.Total` ⇒ reject) but source `alreadyApplied` from the fold via `AppliedToInvoiceAsync`.
  - `ListInvoiceViewsAsync` inline `applied` dictionary → build it once from the Invoice-axis fold (as in Step 3).
  - `GetCustomerCreditBalanceAsync` → the Customer Credits Customer-axis fold (same as Step 3's `credit`).
  Remove the now-unused private allocation-summing helpers if they become dead here (or leave `CustomerAccountBuilder.AppliedByInvoice` for Task 8 to delete).

- [ ] **Step 5: Run the whole Receivables suite.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`. Expected: all PASS — the derived numbers are identical because the ledger and the (still-present) allocations agree; the source is now the ledger.

- [ ] **Step 6: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables/CustomerAccountService.cs \
        Modules/Receivables/Accounting101.Receivables/PaymentService.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/FoldReadTests.cs \
        <DI registration file if changed>
git commit -m "feat(receivables): derive AR balances from ledger folds, not module allocations

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 8: Delete `Allocation[]` storage

With every write dimensioned and every read folding the ledger, remove the second source: `Allocation[]` on the persisted bodies, the `Payment.Allocated`/`Unapplied` accessors' dependence on it, and the dead `CustomerAccountBuilder.AppliedByInvoice`.

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/Payment.cs` (drop `Allocations` from `Payment`; recompute `Allocated`/`Unapplied` from the entry, or drop them if unused)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentBody.cs` and `DispositionBodies.cs` (drop `Allocations` from the persisted bodies; the REQUEST DTOs the endpoints accept keep allocations and pass them to the composer)
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` (stop persisting/reading `Allocations`)
- Modify: `Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs` (delete `AppliedByInvoice`; adjust any signature that took allocation-bearing lists it no longer needs)
- Test: adjust any test constructing a `Payment`/body with `Allocations`; add a test asserting no allocation array is persisted.

**Interfaces:**
- Consumes: the recipes consume allocations from the REQUEST at compose time (unchanged); reads fold the ledger (Task 7).
- Produces: persisted payment/disposition documents with NO allocation array. `Payment.Allocated` = sum of the payment entry's AR credit lines (via `GetEntriesBySourceRefAsync`) or, if `Allocated`/`Unapplied` are only used by now-folded reads, removed entirely.

- [ ] **Step 1: Write the failing test.** Add to `FoldReadTests.cs` (or a new `NoAllocationStorageTests.cs`) a test that records a payment and asserts the persisted payment document carries no allocation array — read it back through the store/endpoint and assert the shape. If `Payment` no longer exposes `Allocations`, this is a compile-time guarantee; assert instead that the open-balance fold still reflects the payment after a fresh read (proving reads never needed the array):

```csharp
[Fact]
public async Task Payment_persists_no_allocation_array_yet_folds_correctly()
{
    // record payment 30 against invoice (100); re-read via GET the payment; assert no allocations surfaced
    // assert invoice fold open == 70 (reads rely only on the ledger)
}
```

- [ ] **Step 2: Run to verify it fails / drives the change.** Run: `dotnet test ... --filter "FullyQualifiedName~NoAllocationStorage OR FullyQualifiedName~persists_no_allocation"`. Expected: FAILS to compile once `Payment.Allocations` is referenced by the test-as-written, or asserts the old array still present — proceed to remove it.

- [ ] **Step 3: Remove `Allocations` from the persisted bodies and `Payment`.** Delete the `Allocations` property from `PaymentBody`, `WriteOffBody`, `CreditNoteBody`, `CreditApplicationBody` (persisted shapes) and from `Payment`. Keep the endpoint REQUEST DTOs (what the caller POSTs) carrying allocations — the endpoint passes `request.Allocations` straight into `ComposePayment`/`ComposeWriteOff`/etc. and persists only the non-allocation body. If the request and persisted body are currently the SAME type, split them: introduce a request record with allocations, persist a body without. Recompute `Payment.Allocated`/`Unapplied` from the payment entry (sum of its AR credit lines via `ledger.GetEntriesBySourceRefAsync`) if any caller still needs them; otherwise delete both accessors and update callers to the fold.

- [ ] **Step 4: Delete the dead builder fold.** Remove `CustomerAccountBuilder.AppliedByInvoice` and any now-unused parameters/overloads. `OpenInvoices`, `Aging`, `ArBalance`, `Statement`, `CreditActivity` remain (they receive the fold-sourced `applied` from Task 7). Fix the compile.

- [ ] **Step 5: Update tests that built allocations into persisted bodies.** Any test that constructed a `PaymentBody`/`Payment` with `Allocations` now constructs the request DTO with allocations and reads results via folds. Update them; do not weaken assertions.

- [ ] **Step 6: Run the whole Receivables suite.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`. Expected: all PASS. `Allocation` (the shared type) may still exist for the request DTOs; that is fine.

- [ ] **Step 7: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables/Payment.cs \
        Modules/Receivables/Accounting101.Receivables/PaymentBody.cs \
        Modules/Receivables/Accounting101.Receivables/DispositionBodies.cs \
        Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs \
        Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs \
        <changed request-DTO + test files>
git commit -m "refactor(receivables): delete Allocation[] storage — the ledger dimension is the allocation

Persisted payment/disposition documents no longer carry an allocation array;
the per-invoice split lives only as ledger dimensions. Reads fold the ledger.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 9: Proof suite + whole-solution reconciliation

Codify the spec §9 proof obligations as one explicit E2E suite, and confirm the whole solution is green.

**Files:**
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ArLedgerFirstProofTests.cs` (new)

**Interfaces:**
- Consumes: everything built in Tasks 1–8, through the `ReceivablesHostFixture` HTTP surface and the module services.

- [ ] **Step 1: Write the proof suite.** Create `ArLedgerFirstProofTests.cs` with one test per obligation (reuse the `ReceivablesIssueTests` setup helpers):
  1. Issue → invoice Invoice-fold = full total.
  2. Partial payment (one allocation) → the invoice's fold reduces by the allocation.
  3. **Split payment across two invoices → each invoice's fold reduces by its own allocation.**
  4. Customer-axis fold = sum of that customer's open invoices.
  5. Over-application (allocation > invoice open balance, read from the fold) → rejected.
  6. Raw A/R line missing the Invoice tag → 422 (may reference the Task-6 test rather than duplicate).
  7. A write-off relieving an invoice → that invoice's fold reduces by the write-off amount.
  8. **Void the invoice's entry through the module's void surface → the invoice's open-balance fold drops to 0 in the same read** (exercises the shipped guard; the module drives the void with its credential).
  9. After a payment, the persisted payment document carries no allocation array (compile-time or shape assertion).

Each test asserts against the ledger fold (`GetSubledgerAsync` or `GET /subledger[/reconciliation]`), not module-stored amounts.

- [ ] **Step 2: Run the proof suite.** Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~ArLedgerFirstProof"`. Expected: all PASS.

- [ ] **Step 3: Run the whole solution.** Run: `dotnet test Accounting101.slnx`. Expected: all PASS. Triage any failure: an unconverted module or a test that assumed A/R required only Customer / that a payment stored allocations. Fix per the same discipline (re-point to the fold; never weaken). Because A/R's two-dimension requirement is scoped to the A/R account only, other modules' single-dimension accounts are unaffected.

- [ ] **Step 4: Commit.**
```bash
git add Modules/Receivables/Accounting101.Receivables.Tests/ArLedgerFirstProofTests.cs \
        <any reconciled test files>
git commit -m "test(receivables): AR ledger-first proof suite — single source of truth end to end

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the executor

- **Staging order is the safety net:** recipes gain the Invoice tag additively (Tasks 3–5) BEFORE A/R requires it (Task 6); reads switch to folds (Task 7) BEFORE `Allocation[]` is deleted (Task 8). Never reorder these — flipping the requirement before a recipe is converted, or deleting allocations before reads fold, breaks the module mid-plan.
- **The request-vs-persisted split (Task 8) is the subtlest change.** Today a body type may serve as both the POST payload and the stored document. Allocations must survive on the *request* (the caller chooses invoices) but vanish from the *stored* document. If they are one type, split them; if already separate, only the stored one loses `Allocations`.
- **Sign of the customer-credit fold (Task 7 Step 3):** Customer Credits is a liability (credit-normal); the debit-positive fold reads its balance negative. Present available credit as a positive number and assert the sign with a test.
- **Do not weaken tests during the recipe conversions (Tasks 4/5) or the whole-solution reconciliation (Task 9).** A test that asserted one aggregate AR line is updated to assert N per-allocation lines summing to the same total, each Invoice-tagged — never deleted or loosened.
- **Refund stays as-is** — it relieves no A/R and carries no Invoice dimension.
- Aging/statement/credit-activity are NOT rebuilt here; they keep passing because their `applied` input is now ledger-sourced. A later cycle may fold them natively.
