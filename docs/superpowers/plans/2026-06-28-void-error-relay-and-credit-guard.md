# Void Error Relay + Negative-Credit Guard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Relay the engine's real status/reason from module disposition paths instead of 500, and refuse any payment void that would drive Customer/Vendor Credits negative.

**Architecture:** Receivables already has a typed `LedgerClientException` (status + reason) and relays it from `IssueInvoice`; widen that relay to its other ledger-touching handlers. Payables has no such type — introduce it, make its `HttpLedgerClient` throw it (mirroring AR's parse), and relay it from the AP endpoints. Add a negative-credit guard to both `VoidPaymentAsync` services.

**Tech Stack:** C# / .NET 10, xUnit, the existing module test projects (service-level fakes for unit tests; `WebApplicationFactory` + shared EphemeralMongo for E2E).

## Global Constraints

- Product behavior changes are exactly two: (a) ledger refusals relayed (no 500), (b) a 409 guard on payment void. No other behavior changes.
- The negative-credit guard lives ONLY on `VoidPaymentAsync` (AR `PaymentService`, AP `BillPaymentService`) — only payments create credit. The guard condition is `payment.Unapplied > 0 && creditBalance - payment.Unapplied < 0m` → throw `InvalidOperationException` (endpoints already map → 409).
- AP's `LedgerClientException` and its `EnsureSuccessAsync`/`ReasonFrom` parse logic are a faithful port of the Receivables versions (ValidationProblemDetails `errors` → ProblemDetails `detail` → raw body → status phrase).
- Relay catch is exactly: `catch (LedgerClientException ex) { return Results.Problem(ex.Reason, statusCode: ex.StatusCode); }`, placed after the existing `catch (InvalidOperationException …)` in each handler. `LedgerClientException` is not an `InvalidOperationException`, so both coexist.
- Do NOT build deferred items (credit-application void, discovery view, refund-as-cash semantics, source lineage).
- Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Reference: existing patterns to mirror

- AR typed exception: `Modules/Receivables/Accounting101.Receivables/LedgerClientException.cs` — `LedgerClientException(int statusCode, string reason)` with `int StatusCode`, `string Reason`.
- AR parse: `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs` `EnsureSuccessAsync` + `ReasonFrom` (lines 96–158).
- AR relay example: `ReceivablesEndpoints.cs:98` (`IssueInvoice`).
- AR ledger-client unit tests: `Modules/Receivables/Accounting101.Receivables.Tests/HttpLedgerClientTests.cs` (`CapturingHandler` pattern).
- Service unit-test harnesses: AR `PaymentServiceTests.SetupWithIssuedInvoiceAsync`; AP `BillPaymentServiceTests.SetupWithEnteredBillAsync` (both expose `FakeLedgerClient` + InMemory stores).

---

### Task 1: Payables — introduce `LedgerClientException` + typed ledger errors

**Files:**
- Create: `Modules/Payables/Accounting101.Payables/LedgerClientException.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`
- Create (test): `Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs`

**Interfaces:**
- Produces (used by Task 2): `Accounting101.Payables.LedgerClientException` with `int StatusCode`, `string Reason`. AP `HttpLedgerClient` throws it on any non-2xx ledger response.

- [ ] **Step 1: Write the failing unit tests**

Create `Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Payables.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Http;

namespace Accounting101.Payables.Tests;

public sealed class HttpLedgerClientTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private static IHttpContextAccessor Context()
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Headers.Authorization = "DevToken abc";
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static HttpLedgerClient ClientFor(HttpResponseMessage response)
    {
        HttpClient http = new(new StubHandler(response)) { BaseAddress = new Uri("http://engine.local") };
        return new HttpLedgerClient(http, Context(), new ModuleCredential("test-key", "test-secret"));
    }

    [Fact]
    public async Task Post_throws_typed_ledger_exception_carrying_status_and_detail()
    {
        HttpLedgerClient client = ClientFor(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { title = "Conflict", status = 409, detail = "Period is closed through 2024-03-31." }),
        });
        PostEntryRequest entry = new(null, new DateOnly(2024, 3, 31), "BILL-1", null,
            [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.PostAsync(Guid.NewGuid(), entry));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("closed", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Void_throws_typed_ledger_exception_on_forbidden()
    {
        HttpLedgerClient client = ClientFor(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { title = "Forbidden", status = 403, detail = "Reverse requires the Approver role." }),
        });

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.VoidAsync(Guid.NewGuid(), Guid.NewGuid(), new VoidRequest("x")));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Approver", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_throws_with_field_level_text_on_validation_problem()
    {
        var body = new
        {
            title = "One or more validation errors occurred.", status = 422,
            detail = "One or more fields are invalid.",
            errors = new Dictionary<string, string[]> { ["lines[0].accountId"] = ["Account 2000 requires a Vendor dimension."] },
        };
        HttpLedgerClient client = ClientFor(new HttpResponseMessage((HttpStatusCode)422) { Content = JsonContent.Create(body) });
        PostEntryRequest entry = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.PostAsync(Guid.NewGuid(), entry));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("lines[0].accountId", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000", ex.Reason);
        Assert.DoesNotContain("One or more fields are invalid", ex.Reason);
    }
}
```

- [ ] **Step 2: Run the tests, verify they FAIL**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~HttpLedgerClientTests" --nologo`
Expected: FAIL to compile — `LedgerClientException` does not exist yet in Payables.

- [ ] **Step 3: Create the Payables `LedgerClientException`**

Create `Modules/Payables/Accounting101.Payables/LedgerClientException.cs`:

```csharp
namespace Accounting101.Payables;

/// <summary>
/// A ledger call returned a non-success status. Carries the engine's HTTP status code and its reason
/// (the ProblemDetails <c>detail</c>, or the raw body) so the module can surface the real cause — a
/// closed-period 409, an unbalanced-entry 422, a reverse-forbidden 403 — instead of letting it escape
/// as an opaque 500.
/// </summary>
public sealed class LedgerClientException(int statusCode, string reason)
    : Exception($"Ledger request failed ({statusCode}): {reason}")
{
    /// <summary>The HTTP status the engine returned (e.g. 403, 409, 422).</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The engine's human-readable reason, suitable to relay to the caller.</summary>
    public string Reason { get; } = reason;
}
```

- [ ] **Step 4: Make AP `HttpLedgerClient` throw it**

In `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`:

Add usings at the top (after the existing `using` lines):
```csharp
using System.Text;
using System.Text.Json;
```

Replace each of the five `response.EnsureSuccessStatusCode();` calls (in `PostAsync`, `ApproveAsync`, `ReverseAsync`, `VoidAsync`, `GetEntriesBySourceRefAsync`) with:
```csharp
        await EnsureSuccessAsync(response, cancellationToken);
```

Then add these two private helpers to the class (e.g. just before the existing `Forwarded` helper) — a faithful port of the Receivables versions:
```csharp
    /// <summary>Throw a typed <see cref="LedgerClientException"/> carrying the engine's status and reason on
    /// any non-success response, so the module's endpoints can relay the real cause instead of a 500.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

    /// <summary>Best available reason from the response body: ValidationProblemDetails <c>errors</c> flattened
    /// to "field: msg; …", else ProblemDetails <c>detail</c>, else the raw body, else the status phrase.</summary>
    private static string ReasonFrom(string body, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("errors", out JsonElement errors)
                        && errors.ValueKind == JsonValueKind.Object)
                    {
                        StringBuilder sb = new();
                        foreach (JsonProperty prop in errors.EnumerateObject())
                        {
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(prop.Name).Append(": ");
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                                sb.Append(string.Join(", ", prop.Value.EnumerateArray().Select(m => m.GetString() ?? string.Empty)));
                            else
                                sb.Append(prop.Value.GetRawText().Trim('"'));
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }

                    if (root.TryGetProperty("detail", out JsonElement detail)
                        && detail.ValueKind == JsonValueKind.String
                        && detail.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }
            catch (JsonException) { /* not JSON — relay the raw body */ }

            return body.Trim();
        }

        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
    }
```

- [ ] **Step 5: Run the tests, verify they PASS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~HttpLedgerClientTests" --nologo`
Expected: 3/3 PASS.

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/LedgerClientException.cs \
        Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs \
        Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs
git commit -m "$(cat <<'EOF'
feat(payables): typed LedgerClientException from the ledger client

Mirrors Receivables: the AP HttpLedgerClient now parses a non-2xx engine
response and throws LedgerClientException(status, reason) instead of the
bare HttpRequestException from EnsureSuccessStatusCode, so endpoints can
relay the real cause.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Payables — relay ledger errors from the endpoints

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs`
- Create (test): `Modules/Payables/Accounting101.Payables.Tests/LedgerErrorRelayE2eTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Payables.LedgerClientException` (Task 1); `BillSettlementScenario` helpers (existing, `Accounting101.Payables.Tests`).

- [ ] **Step 1: Write the failing E2E test**

Create `Modules/Payables/Accounting101.Payables.Tests/LedgerErrorRelayE2eTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>A ledger refusal on a disposition path is relayed with the engine's real status (a 4xx),
/// not an opaque 500. Exercised via a Clerk attempting to void a posted bill payment — reversing a
/// posted entry requires the Approver role, so the engine refuses (403); the module must relay it.</summary>
public sealed class LedgerErrorRelayE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Clerk_voiding_a_posted_bill_payment_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(bill, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // The clerk lacks Reverse permission; voiding a *posted* payment reverses the entry → engine refuses.
        HttpResponseMessage resp = await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/bill-payments/{pay.Id}/void", new VoidReasonRequest("oops"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.InRange((int)resp.StatusCode, 400, 499);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
    }
}
```

- [ ] **Step 2: Run it, verify it FAILS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~LedgerErrorRelayE2eTests" --nologo`
Expected: FAIL — the response is currently 500 (the unhandled `LedgerClientException` escapes the void handler).

- [ ] **Step 3: Add the relay catch to the AP endpoints**

In `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs`, add this catch immediately after the existing `catch (InvalidOperationException ex)` block in each of `DraftBill`/`EnterBill`/`VoidBill`/`RecordPayment`/`VoidPayment`/`ApplyCredit` — i.e. every handler that calls the ledger. (`CreateVendor`, `GetBill`, `ListBills`, `GetCreditBalance` don't touch the ledger; leave them.)

```csharp
        catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
```

Concretely, the handlers to update (each already has a `try { … } catch (InvalidOperationException ex) { … }`): `DraftBill` (line ~33), `EnterBill` (~50), `VoidBill` (~64), `RecordPayment` (~104), `VoidPayment` (~120), `ApplyCredit` (~134). Add the new catch after each one's `InvalidOperationException` catch.

- [ ] **Step 4: Run it, verify it PASSES**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~LedgerErrorRelayE2eTests" --nologo`
Expected: PASS (relayed 4xx, not 500). If the relayed status is something other than 403, that's fine — the test only requires a non-500 client error with a reason.

- [ ] **Step 5: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs \
        Modules/Payables/Accounting101.Payables.Tests/LedgerErrorRelayE2eTests.cs
git commit -m "$(cat <<'EOF'
fix(payables): relay ledger refusals instead of 500

Every AP handler that calls the ledger now catches LedgerClientException
and relays the engine's status + reason. A clerk voiding a posted bill
payment gets the engine's 4xx, not an opaque 500.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Receivables — relay ledger errors from the record + void endpoints

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`
- Create (test): `Modules/Receivables/Accounting101.Receivables.Tests/LedgerErrorRelayE2eTests.cs`

**Interfaces:**
- Consumes: existing `Accounting101.Receivables.LedgerClientException`; `SettlementScenario` helpers (existing).

- [ ] **Step 1: Write the failing E2E test**

Create `Modules/Receivables/Accounting101.Receivables.Tests/LedgerErrorRelayE2eTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>A ledger refusal on a disposition path is relayed with the engine's real status (a 4xx),
/// not an opaque 500. Exercised via a Clerk attempting to void a posted payment — reversing a posted
/// entry requires the Approver role, so the engine refuses (403); the module must relay it.</summary>
public sealed class LedgerErrorRelayE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    [Fact]
    public async Task Clerk_voiding_a_posted_payment_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(invoice, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // The clerk lacks Reverse permission; voiding a *posted* payment reverses the entry → engine refuses.
        HttpResponseMessage resp = await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/payments/{pay.Id}/void", new VoidInvoiceRequest("oops"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.InRange((int)resp.StatusCode, 400, 499);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
    }
}
```

- [ ] **Step 2: Run it, verify it FAILS**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~LedgerErrorRelayE2eTests" --nologo`
Expected: FAIL — currently 500 (the void handler doesn't catch `LedgerClientException`).

- [ ] **Step 3: Add the relay catch to the AR ledger-touching handlers**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`, add this catch immediately after the existing `catch (InvalidOperationException ex)` block in each handler listed below:

```csharp
        catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
```

Handlers to update (all post to / transition the ledger and currently catch only `InvalidOperationException`):
`RecordPayment`, `ApplyCredit`, `RecordWriteOff`, `RecordCreditNote`, `RecordRefund`, `VoidPayment`, `VoidWriteOff`, `VoidCreditNote`, `VoidRefund`, `VoidInvoice`.

Do NOT modify `IssueInvoice` (it already has this catch) or non-ledger handlers (`CreateCustomer`, `DraftInvoice`, `EditInvoice`, `DiscardInvoice`, `GetInvoice`, `ListInvoices`, `GetCreditBalance`).

- [ ] **Step 4: Run it, verify it PASSES**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~LedgerErrorRelayE2eTests" --nologo`
Expected: PASS (relayed 4xx, not 500).

- [ ] **Step 5: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/LedgerErrorRelayE2eTests.cs
git commit -m "$(cat <<'EOF'
fix(receivables): relay ledger refusals from record + void endpoints

Widens the LedgerClientException relay (already on IssueInvoice) to every
other ledger-touching handler, so a clerk voiding a posted payment — or any
ledger refusal on a record/void path — gets the engine's 4xx, not a 500.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Receivables — negative-credit guard on payment void

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`
- Modify (test): `Modules/Receivables/Accounting101.Receivables.Tests/PaymentServiceTests.cs`

**Interfaces:**
- Consumes: `PaymentService.GetCustomerCreditBalanceAsync` (existing); `Payment.Unapplied`/`CustomerId` (existing).

- [ ] **Step 1: Write the failing unit tests**

Add to `Modules/Receivables/Accounting101.Receivables.Tests/PaymentServiceTests.cs` (reuse the file's `SetupWithIssuedInvoiceAsync` harness + `Harness.Invoices`):

```csharp
    private async Task<Invoice> IssueAnotherInvoiceAsync(Harness h, Guid clientId, Guid customerId, decimal total)
    {
        Invoice draft = await h.Invoices.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null, [new LineBody("Services", 1m, total, false)]));
        return await h.Invoices.PromoteDraftAsync(clientId, draft.Id);
    }

    [Fact]
    public async Task Voiding_a_payment_whose_credit_was_applied_is_rejected()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice1) = await SetupWithIssuedInvoiceAsync(100m);
        // Overpay invoice1 by 50 → $50 customer credit.
        Payment pay = await h.Service.RecordPaymentAsync(clientId,
            new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice1.Id, 100m)]));
        // Apply that $50 credit to a second invoice → pool now 0.
        Invoice invoice2 = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);
        await h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationBody(customerId, new DateOnly(2026, 4, 1), [new Allocation(invoice2.Id, 50m)]));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.VoidPaymentAsync(clientId, pay.Id));
        Assert.Contains("already been applied", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The credit balance never went negative; the payment is still active.
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
    }

    [Fact]
    public async Task Voiding_a_payment_whose_credit_is_still_available_succeeds()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice1) = await SetupWithIssuedInvoiceAsync(100m);
        Payment pay = await h.Service.RecordPaymentAsync(clientId,
            new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice1.Id, 100m)]));

        Payment voided = await h.Service.VoidPaymentAsync(clientId, pay.Id);

        Assert.True(voided.Voided);
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
    }

    [Fact]
    public async Task Voiding_one_overpayment_is_allowed_when_other_credit_covers_the_spend()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice1) = await SetupWithIssuedInvoiceAsync(100m);
        // Two overpayments → pool $100.
        Payment payA = await h.Service.RecordPaymentAsync(clientId,
            new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice1.Id, 100m)]));
        Invoice invoice2 = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);
        await h.Service.RecordPaymentAsync(clientId,
            new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice2.Id, 100m)]));
        // Spend $50 of credit on a third invoice → pool $50 remains.
        Invoice invoice3 = await IssueAnotherInvoiceAsync(h, clientId, customerId, 100m);
        await h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationBody(customerId, new DateOnly(2026, 4, 1), [new Allocation(invoice3.Id, 50m)]));

        // Voiding payA ($50 unapplied) is allowed: pool ($50) still covers it (payB's credit absorbs the spend).
        Payment voided = await h.Service.VoidPaymentAsync(clientId, payA.Id);
        Assert.True(voided.Voided);
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
    }
```

- [ ] **Step 2: Run them, verify the first FAILS**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~PaymentServiceTests.Voiding_a_payment_whose_credit_was_applied_is_rejected" --nologo`
Expected: FAIL — no guard yet, so the void succeeds (no exception) and drives credit to −50.

- [ ] **Step 3: Add the guard**

In `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`, in `VoidPaymentAsync`, immediately after the existing `if (payment.Voided) throw …` check and before the `await ledger.GetEntriesBySourceRefAsync(...)`/reverse logic, add:

```csharp
        // A void reverses the whole payment, including the overpayment that landed as customer credit. If that
        // credit has since been applied or refunded, removing it would drive the credit balance negative (a
        // debit balance on a liability) — a corrupt state. Refuse; the consuming application/refund must be
        // reversed first.
        if (payment.Unapplied > 0m)
        {
            decimal creditBalance = await GetCustomerCreditBalanceAsync(clientId, payment.CustomerId, ct);
            if (creditBalance - payment.Unapplied < 0m)
                throw new InvalidOperationException(
                    $"Cannot void payment {paymentId}: its overpayment credit ({payment.Unapplied:C}) has already " +
                    $"been applied or refunded (available credit is only {creditBalance:C}). Reverse the credit " +
                    $"application(s)/refund(s) first, then void this payment.");
        }
```

- [ ] **Step 4: Run all three guard tests, verify they PASS**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~PaymentServiceTests.Voiding" --nologo`
Expected: all three (rejected / still-available / other-credit-covers) PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentService.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/PaymentServiceTests.cs
git commit -m "$(cat <<'EOF'
fix(receivables): refuse a payment void that would make customer credit negative

Voiding a payment whose overpayment credit was already applied/refunded
would push Customer Credits to a debit balance (corrupt). Guard
VoidPaymentAsync: block with a 409-mapped message when creditBalance minus
the payment's unapplied amount would go below zero. Fungible-correct — still
allows the void when other credit covers the spend.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Payables — negative-credit guard on bill-payment void

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs`
- Modify (test): `Modules/Payables/Accounting101.Payables.Tests/BillPaymentServiceTests.cs`

**Interfaces:**
- Consumes: `BillPaymentService.GetVendorCreditBalanceAsync` (existing); `BillPayment.Unapplied`/`VendorId` (existing).

- [ ] **Step 1: Write the failing unit tests**

Add to `Modules/Payables/Accounting101.Payables.Tests/BillPaymentServiceTests.cs` (reuse `SetupWithEnteredBillAsync` + `Harness.BillStore`):

```csharp
    private async Task<Bill> EnterAnotherBillAsync(Harness h, Guid clientId, Guid vendorId, decimal total)
    {
        Bill draft = await h.BillStore.CreateDraftAsync(clientId, new BillBody(
            vendorId, new DateOnly(2026, 3, 1), null, null, null, [new BillLineBody("Rent", total, Guid.NewGuid())]));
        return await h.BillStore.FinalizeAsync(clientId, draft.Id);
    }

    [Fact]
    public async Task Voiding_a_bill_payment_whose_credit_was_applied_is_rejected()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill1) = await SetupWithEnteredBillAsync(100m);
        // Overpay bill1 by 50 → $50 vendor credit.
        BillPayment pay = await h.Payments.RecordPaymentAsync(clientId,
            new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(bill1.Id, 100m)]));
        // Apply that $50 credit to a second bill → pool now 0.
        Bill bill2 = await EnterAnotherBillAsync(h, clientId, vendorId, 100m);
        await h.Payments.RecordCreditApplicationAsync(clientId,
            new VendorCreditApplicationBody(vendorId, new DateOnly(2026, 4, 1), [new Allocation(bill2.Id, 50m)]));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Payments.VoidPaymentAsync(clientId, pay.Id));
        Assert.Contains("already been applied", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0m, await h.Payments.GetVendorCreditBalanceAsync(clientId, vendorId));
    }

    [Fact]
    public async Task Voiding_a_bill_payment_whose_credit_is_still_available_succeeds()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill1) = await SetupWithEnteredBillAsync(100m);
        BillPayment pay = await h.Payments.RecordPaymentAsync(clientId,
            new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(bill1.Id, 100m)]));

        BillPayment voided = await h.Payments.VoidPaymentAsync(clientId, pay.Id);

        Assert.True(voided.Voided);
        Assert.Equal(0m, await h.Payments.GetVendorCreditBalanceAsync(clientId, vendorId));
    }

    [Fact]
    public async Task Voiding_one_overpayment_is_allowed_when_other_vendor_credit_covers_the_spend()
    {
        (Harness h, Guid clientId, Guid vendorId, Bill bill1) = await SetupWithEnteredBillAsync(100m);
        BillPayment payA = await h.Payments.RecordPaymentAsync(clientId,
            new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(bill1.Id, 100m)]));
        Bill bill2 = await EnterAnotherBillAsync(h, clientId, vendorId, 100m);
        await h.Payments.RecordPaymentAsync(clientId,
            new BillPaymentBody(vendorId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(bill2.Id, 100m)]));
        Bill bill3 = await EnterAnotherBillAsync(h, clientId, vendorId, 100m);
        await h.Payments.RecordCreditApplicationAsync(clientId,
            new VendorCreditApplicationBody(vendorId, new DateOnly(2026, 4, 1), [new Allocation(bill3.Id, 50m)]));

        BillPayment voided = await h.Payments.VoidPaymentAsync(clientId, payA.Id);
        Assert.True(voided.Voided);
        Assert.Equal(0m, await h.Payments.GetVendorCreditBalanceAsync(clientId, vendorId));
    }
```

- [ ] **Step 2: Run them, verify the first FAILS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillPaymentServiceTests.Voiding_a_bill_payment_whose_credit_was_applied_is_rejected" --nologo`
Expected: FAIL — no guard yet.

- [ ] **Step 3: Add the guard**

In `Modules/Payables/Accounting101.Payables/BillPaymentService.cs`, in `VoidPaymentAsync`, immediately after the existing `if (payment.Voided) throw …` check and before the `await ledger.GetEntriesBySourceRefAsync(...)` logic, add:

```csharp
        // A void reverses the whole payment, including the overpayment that landed as vendor credit. If that
        // credit has since been applied, removing it would drive the credit balance negative (a credit balance
        // on the Vendor Credits asset) — a corrupt state. Refuse; the consuming application must be reversed first.
        if (payment.Unapplied > 0m)
        {
            decimal creditBalance = await GetVendorCreditBalanceAsync(clientId, payment.VendorId, ct);
            if (creditBalance - payment.Unapplied < 0m)
                throw new InvalidOperationException(
                    $"Cannot void payment {paymentId}: its overpayment credit ({payment.Unapplied:C}) has already " +
                    $"been applied (available credit is only {creditBalance:C}). Reverse the vendor credit " +
                    $"application(s) first, then void this payment.");
        }
```

- [ ] **Step 4: Run all three guard tests, verify they PASS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillPaymentServiceTests.Voiding" --nologo`
Expected: all three PASS.

- [ ] **Step 5: Run both full module suites — confirm no regressions**

Run:
`dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --nologo`
`dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --nologo`
Expected: both green. (Shared-Mongo infra means one mongod per project; no replica-set flake.)

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/BillPaymentService.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillPaymentServiceTests.cs
git commit -m "$(cat <<'EOF'
fix(payables): refuse a bill-payment void that would make vendor credit negative

Mirrors the receivables guard: block VoidPaymentAsync when voiding would push
the vendor credit balance below zero because the overpayment credit was
already applied. Fungible-correct — allowed when other credit covers the spend.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- Finding #1 AR (widen relay to record + void handlers) → Task 3. ✓
- Finding #1 AP (introduce LedgerClientException + parse + relay) → Tasks 1 (type + client) + 2 (endpoints). ✓
- Finding #2 guard AR → Task 4; AP → Task 5. ✓
- Guard only on payment void, condition `Unapplied > 0 && creditBalance - Unapplied < 0` → Tasks 4/5 Step 3. ✓
- Fungible-correct "allowed when other credit covers" → Tasks 4/5 third test. ✓
- Deferred items NOT built → no task creates a credit-application void / discovery / lineage. ✓
- Tests: #1 relay E2E (both) + AP HttpLedgerClient unit tests; #2 service unit tests (both). ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code; commands explicit with expected output. The endpoint-catch tasks show the exact catch block once and enumerate every insertion point by handler name (identical block by design, not a placeholder). ✓

**3. Type consistency:** `LedgerClientException(int statusCode, string reason)` with `StatusCode`/`Reason` identical AR↔AP. Guard uses `GetCustomerCreditBalanceAsync`/`payment.CustomerId` (AR) and `GetVendorCreditBalanceAsync`/`payment.VendorId` (AP) — both confirmed to exist with these signatures. Test harness helpers (`SetupWithIssuedInvoiceAsync`/`SetupWithEnteredBillAsync`, `InMemoryInvoiceStore.CreateDraftAsync`/`PromoteDraftAsync`, `InMemoryBillStore.CreateDraftAsync`/`FinalizeAsync`, `InvoiceBody`/`LineBody`, `BillBody`/`BillLineBody`) match the existing test files. Relay catch signature matches `IssueInvoice`'s existing one. ✓
