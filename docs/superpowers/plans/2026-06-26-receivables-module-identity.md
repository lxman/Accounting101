# Receivables Module-Identity Posting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Migrate the Receivables module to post under its `receivables` credential (`viaModule="receivables"`) like Payables/Payroll/Cash, and make the engine's `/entries/validate` pre-flight module-aware so the issue-invoice path survives slice 6.

**Architecture:** Two changes. (1) Engine: `ValidateEntry` uses `ResolveForPostAsync` (module-aware, backward-compatible) instead of `ResolveAsync(Permission.Post)`. (2) Receivables `HttpLedgerClient` injects the keyed `receivables` `ModuleCredential` and sends `X-Module-Key`/`X-Module-Secret` on `PostAsync` + `ValidateAsync`, preserving its `LedgerClientException` error handling. Plus a `ModuleViaReceivablesTests` proof.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, xUnit + EphemeralMongo + `WebApplicationFactory<Program>`.

**Spec:** `docs/superpowers/specs/2026-06-26-receivables-module-identity-design.md`

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Engine change MUST be backward-compatible (existing user-`Post` validate/post callers unaffected) — proven by existing engine tests staying green.
- Mirror Payables' credential pattern (`Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`); KEEP Receivables' richer `EnsureSuccessAsync`/`ReasonFrom`/`LedgerClientException` error handling (do not replace with bare `EnsureSuccessStatusCode`).
- Only `PostAsync` + `ValidateAsync` send the credential; `ApproveAsync`/`ReverseAsync`/`VoidAsync` forward the user token only.
- Run test classes one at a time (host-boot flakiness). Stage explicit file lists; do NOT commit in a worktree.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## Task 1: Make `/entries/validate` module-aware (engine)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (the `ValidateEntry` handler, ~line 125-133)
- Test: an engine test project class that exercises `/entries/validate` (e.g. `Backend/Accounting101.Ledger.Api.Tests/...`) — find the existing validate or module-post test and mirror it.

**Interfaces:**
- `LedgerGateway.ResolveForPostAsync(ClaimsPrincipal user, Guid clientId, IModuleAuthenticator moduleAuth, CancellationToken ct) → Task<LedgerContext>` (exists; falls back to `ResolveAsync(Permission.Post)` when no module credential).
- `PostEntry` already takes `IModuleAuthenticator moduleAuth` and calls `ResolveForPostAsync` — copy that wiring into `ValidateEntry`.

- [ ] **Step 1: Write/adjust the failing test.** Locate the existing engine test(s) for `/entries/validate` and for module-credential posting. Add a test asserting `POST /entries/validate` authorizes when called with a **module credential** (`X-Module-Key`/`X-Module-Secret`) by a user who would otherwise rely on the module path — mirroring the existing module-post test but hitting `/entries/validate` and asserting `200 {valid:true}`. If the engine test harness for module credentials is not readily reusable, instead add/keep a test asserting `/entries/validate` still returns `200` for a normal `Post`-holding user (no-regression) and rely on the Receivables E2E (Task 2) for the credentialed-validate integration proof. Either way, an existing `Post`-user validate test must remain green.

- [ ] **Step 2: Run, confirm the new/relevant test fails (or, for the no-regression case, confirm the suite is green before the change).**

- [ ] **Step 3: Implement.** In `LedgerEndpoints.cs`, change the `ValidateEntry` signature to include `IModuleAuthenticator moduleAuth` and swap the resolve call:
```csharp
    private static async Task<IResult> ValidateEntry(
        Guid clientId, PostEntryRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveForPostAsync(user, clientId, moduleAuth, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        (IResult? rejection, _) = await ValidateForPostAsync(clientId, request, ctx, cancellationToken);
        return rejection ?? Results.Ok(new EntryValidationResponse(true));
    }
```
(Mirror `PostEntry`'s parameter list/order. `ValidateForPostAsync` is unchanged.)

- [ ] **Step 4: Run, confirm pass** — the new test green; the engine's existing post/validate test classes still green (no regression). Full solution builds 0 warnings.

- [ ] **Step 5: Commit** — `feat(ledger): make /entries/validate module-aware (ResolveForPostAsync)`.

---

## Task 2: Receivables posts under its credential + viaModule proof

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ModuleViaReceivablesTests.cs` (new)

**Interfaces:**
- Consumes: the keyed `ModuleCredential` registered by `AddReceivables` → `AddModule(new ModuleIdentity("receivables"), …)` (resolve via `[FromKeyedServices("receivables")]`).
- Consumes: Task 1's module-aware `/entries/validate`.
- `ModuleCredential` has `.Key` and `.Secret` (string) — see Payables' `HttpLedgerClient`.

- [ ] **Step 1: Write the failing test** — `ModuleViaReceivablesTests.cs`, mirroring `Modules/Payables/Accounting101.Payables.Tests/ModuleViaPayablesTests.cs` (seed SoD client + chart via `ReceivablesHostFixture`, use the same `SetUpChartAsync`/`IssueInvoiceAsync`-style helpers `CashApplicationTests` uses):
```csharp
[Fact]
public async Task Issuing_an_invoice_stamps_ViaModule_receivables()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
    await SetUpChartAsync(controller, clientId);
    Guid customer = await CreateCustomerAsync(clerk, clientId);
    Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 500m);  // issue calls ValidateAsync + PostAsync

    EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
        $"/clients/{clientId}/entries?sourceRef={invoice}"))!;
    Assert.Single(entries);
    Assert.Equal("receivables", entries[0].ViaModule);
}

[Fact]
public async Task Recording_a_payment_stamps_ViaModule_receivables()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
    await SetUpChartAsync(controller, clientId);
    Guid customer = await CreateCustomerAsync(clerk, clientId);
    Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 500m);

    Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
        new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 500m, "wire", [new Allocation(invoice, 500m)])))
        .Content.ReadFromJsonAsync<Payment>())!;

    EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
        $"/clients/{clientId}/entries?sourceRef={payment.Id}"))!;
    Assert.Single(entries);
    Assert.Equal("receivables", entries[0].ViaModule);
}
```
(Reuse the chart-setup + customer + issue helpers from `CashApplicationTests`/`ReceivablesDispositionsE2eTests`; copy them into this class or a shared helper. RED before the change: `ViaModule` is null.)

- [ ] **Step 2: Run, confirm fail** — `ViaModule` is null (raw path).

- [ ] **Step 3: Implement.** In `HttpLedgerClient.cs`, add the keyed credential to the primary constructor and send the headers on `PostAsync` + `ValidateAsync` only. Keep `Forwarded`, `EnsureSuccessAsync`, `ReasonFrom` exactly as they are.
```csharp
using Microsoft.Extensions.DependencyInjection;   // add for [FromKeyedServices]
// ...
public sealed class HttpLedgerClient(
    HttpClient http,
    IHttpContextAccessor context,
    [FromKeyedServices("receivables")] ModuleCredential credential) : ILedgerClient
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PostEntryResponse>(cancellationToken))!;
    }

    // ValidateAsync: add the same two header lines after Forwarded(...), before setting Content:
    public async Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/validate");
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }
    // ApproveAsync / ReverseAsync / VoidAsync / GetEntriesBySourceRefAsync unchanged (no credential).
}
```
Confirm `ModuleCredential` is imported (its namespace — see Payables' `using Accounting101.Ledger.Api.Auth;`). The `ReceivablesHostFixture`'s test-server `AddHttpClient<ILedgerClient, HttpLedgerClient>` rewiring must still resolve the keyed credential — since `AddReceivables` registers it, DI resolves it; if the fixture's `ConfigureTestServices` re-registration drops it, the test will fail to construct the client and you'll need to ensure the keyed `ModuleCredential` is registered in the test host (it is, via `AddReceivables`). Verify the existing Receivables E2E classes still construct the client.

- [ ] **Step 4: Run, confirm pass** — `ModuleViaReceivablesTests` green; then re-run `ReceivablesIssueTests`, `CashApplicationTests`, `ReceivablesDispositionsE2eTests` (no regression — the credential path didn't break issue/payment/disposition). Full solution 0 warnings.

- [ ] **Step 5: Commit** — `feat(receivables): post under the receivables credential (viaModule)`.

---

## Final verification
- [ ] `dotnet build Accounting101.slnx -c Debug` → 0 warnings.
- [ ] Run individually: `ModuleViaReceivablesTests`, `ReceivablesIssueTests`, `CashApplicationTests`, `ReceivablesDispositionsE2eTests`, and the engine's validate/post test class — all green.
- [ ] Confirm: invoices + payments via Receivables carry `viaModule="receivables"`; `/entries/validate` is module-aware and still works for `Post` users; Approve/Reverse/Void unchanged; Receivables' `LedgerClientException` error handling preserved.
- [ ] Whole-branch review on the most capable model (auth surface — engine + credential), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- **Spec coverage:** engine validate module-aware (T1), Receivables client credential on Post+Validate (T2), viaModule proof (T2). Scope boundaries (no Clerk-role change, no Approve/Reverse/Void credential) respected.
- **Type consistency:** `ResolveForPostAsync` signature matches `PostEntry`'s usage; `[FromKeyedServices("receivables")] ModuleCredential` matches the AddModule registration key; `ModuleCredential.Key`/`.Secret` per Payables.
- **Open implementer checks:** (a) the engine test harness may or may not have reusable module-credential infra for validate — fall back to the no-regression assertion + Receivables E2E integration proof if so; (b) confirm `ReceivablesHostFixture` still resolves the keyed credential when it rewires the typed client (it should, via `AddReceivables`); (c) keep Receivables' `EnsureSuccessAsync`/`ReasonFrom` — do NOT copy Payables' bare `EnsureSuccessStatusCode`.
