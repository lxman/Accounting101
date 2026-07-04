# Multi-Firm Tenancy — Phase 3b (module entitlement + usage) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the per-client `EnabledModules` field settable and enforced default-closed at the module-authorization chokepoint, and expose a platform usage meter — the billing/entitlement layer on top of Phase 3a's provisioning.

**Architecture:** One new check in `ModuleAccess.AuthorizeAsync` (the single chokepoint both `ScopedDocumentStore` and the module ledger-post path funnel through) reads the firm-scoped client and denies any module not in its `EnabledModules`. A firm-admin `PUT /admin/clients/{clientId}/modules` setter writes the field; a platform-operator `GET /platform/usage` reads a per-firm snapshot. Because enforcement is default-closed, every test fixture that seeds a client which then exercises a module must set `EnabledModules` — the atomic "fixture sweep" that keeps the module suites green.

**Tech Stack:** .NET (C#), ASP.NET Core minimal APIs, MongoDB driver, xUnit + EphemeralMongo (`SharedMongo`), `WebApplicationFactory<Program>`.

## Global Constraints

- Every module refusal is HTTP **403** at the boundary; the distinct `ModuleAccessDecision` values exist only for logging/tests. `NotEntitled` is no exception.
- **Default-closed:** an empty `EnabledModules` denies every module. No grandfather path.
- Entitlement is **firm-scoped**: the check reads the caller firm's own `ControlStore` (the scoped instance already injected into `ModuleAccess`), so a cross-firm `clientId` resolves to `null` → denied.
- The **raw ledger path** (no module credential → `LedgerGateway.ResolveAsync`) is unaffected — it never calls `ModuleAccess`.
- Unknown module keys in the setter are **rejected with 400** (validated against installed `ModuleRegistration`s) — the resolved open question.
- Setter gate: deployment/firm admin (`admin=true`) OR the per-client `admin.client` capability, via `AdminAuthorization.MayAsync` — matching the existing `/admin/clients/{clientId}/...` endpoints.
- New contracts live in `Backend/Accounting101.Ledger.Contracts`; no raw connection strings ever leave the platform surface.

---

## File Structure

**Modify:**
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — add `SetClientModulesRequest`, `ClientModulesResponse`.
- `Backend/Accounting101.Ledger.Contracts/PlatformContracts.cs` — add `UsageResponse`, `FirmUsageResponse`.
- `Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs` — add `NotEntitled` decision + the entitlement check.
- `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs` — add `SetClientModulesAsync`.
- `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` — add `PUT /clients/{clientId}/modules`.
- `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs` — add `GET /usage`.
- Test fixtures (the sweep): `ApiFixture.cs`, `ReceivablesHostFixture.cs`, `PayablesHostFixture.cs`, `PayrollHostFixture.cs`, `CashHostFixture.cs`, `ReconciliationHostFixture.cs`, `DocumentStoreFixture.cs` (Receivables), `PayablesDocumentStoreFixture.cs`.

**Create (tests):**
- `Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs` — the setter endpoint.
- `Backend/Accounting101.Ledger.Api.Tests/PlatformUsageTests.cs` — the usage meter.
- New `[Fact]`s appended to `Backend/Accounting101.Ledger.Api.Tests/ModuleAccessTests.cs` — the `NotEntitled` decision.
- One E2E `[Fact]` appended to a Receivables host test — entitle-then-succeed.

---

## Task 1: Contracts

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/PlatformContracts.cs`

**Interfaces:**
- Produces: `SetClientModulesRequest(IReadOnlyList<string> ModuleKeys)`, `ClientModulesResponse(Guid ClientId, IReadOnlyList<string> ModuleKeys)`, `UsageResponse(IReadOnlyList<FirmUsageResponse> Firms)`, `FirmUsageResponse(Guid FirmId, string Name, int ActiveClients, IReadOnlyDictionary<string,int> ModuleClientCounts)`.

- [ ] **Step 1: Add the admin contracts.** Append to `AdminContracts.cs`:

```csharp
/// <summary>Replace a client's entitled module keys (default-closed access + billing meter). Idempotent.</summary>
public sealed record SetClientModulesRequest(IReadOnlyList<string> ModuleKeys);

/// <summary>A client's entitled module keys as returned by the entitlement setter.</summary>
public sealed record ClientModulesResponse(Guid ClientId, IReadOnlyList<string> ModuleKeys);
```

- [ ] **Step 2: Add the usage contracts.** Append to `PlatformContracts.cs`:

```csharp
/// <summary>Per-firm usage snapshot the future billing subsystem consumes. No pricing logic here.</summary>
public sealed record UsageResponse(IReadOnlyList<FirmUsageResponse> Firms);

/// <summary>One firm's meter: active-client count and, per module key, how many active clients have it enabled.</summary>
public sealed record FirmUsageResponse(
    Guid FirmId, string Name, int ActiveClients, IReadOnlyDictionary<string, int> ModuleClientCounts);
```

- [ ] **Step 3: Build the contracts project.**

Run: `dotnet build Backend/Accounting101.Ledger.Contracts/Accounting101.Ledger.Contracts.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit.**

```bash
git add Backend/Accounting101.Ledger.Contracts/
git commit -m "feat(tenancy): add phase-3b entitlement + usage contracts"
```

---

## Task 2: Entitlement setter (`PUT /admin/clients/{clientId}/modules`)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs` (create)

**Interfaces:**
- Consumes: `SetClientModulesRequest`, `ClientModulesResponse` (Task 1); `AdminAuthorization.MayAsync`, `Capabilities.AdminClient`, `ControlStore.GetClientAsync`/`GetModuleAsync`.
- Produces: `ControlStore.SetClientModulesAsync(Guid clientId, IReadOnlyList<string> moduleKeys, CancellationToken) → Task<bool>` (false = no such client).

> Note: no enforcement exists yet, so this task changes no existing behavior — the setter is purely additive and its tests seed their own clients + modules.

- [ ] **Step 1: Write the failing test.** Create `ClientModulesTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ClientModulesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Deployment_admin_sets_modules_and_they_persist()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["receivables"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ClientRegistration? saved = await control.GetClientAsync(client.ClientId);
        Assert.NotNull(saved);
        Assert.Equal(["receivables"], saved!.EnabledModules);
    }

    [Fact]
    public async Task Unknown_module_key_is_rejected()
    {
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["not-a-real-module"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_client_is_not_found()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{Guid.NewGuid()}/modules", new SetClientModulesRequest([]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_plain_member_without_admin_client_is_forbidden()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Clerk, enabledModules: []);

        HttpResponseMessage response = await client.Http.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules", new SetClientModulesRequest(["receivables"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter FullyQualifiedName~ClientModulesTests`
Expected: FAIL — `SeedClientAsync` has no `enabledModules` parameter and the route 404s. (The `enabledModules` param is added in Task 3's sweep; if Task 3 has not run yet, temporarily add the param here — see Step 3a.)

- [ ] **Step 3a: Add the `enabledModules` param to `ApiFixture.SeedClientAsync` (if not already present from Task 3).** In `ApiFixture.cs`, change the signature and body:

```csharp
public async Task<SeededClient> SeedClientAsync(
    string name = "Acme", bool requireSod = false, LedgerRole role = LedgerRole.Controller,
    IReadOnlyList<string>? enabledModules = null)
{
    Guid clientId = Guid.NewGuid();
    string database = "client_" + clientId.ToString("N");
    Guid userId = Guid.NewGuid();

    ControlStore control = Control();
    IReadOnlyList<string> modules = enabledModules ?? (await control.ListModulesAsync()).Select(m => m.Key).ToList();
    await control.RegisterClientAsync(new ClientRegistration
    {
        Id = clientId,
        Name = name,
        DatabaseName = database,
        RequireSegregationOfDuties = requireSod,
        EnabledModules = modules,
    });
    await control.AddMembershipAsync(userId, clientId, role);

    HttpClient http = ClientFor(userId, $"{name} {role}", ("role", role.ToString()));
    return new SeededClient(clientId, database, userId, http);
}
```

- [ ] **Step 3b: Add `ControlStore.SetClientModulesAsync`.** In `ControlStore.cs`, after `RegisterClientAsync`:

```csharp
/// <summary>Replace a client's entitled module keys (default-closed access gate + billing meter).
/// Returns false when no such client exists in this firm's control DB. Idempotent.</summary>
public async Task<bool> SetClientModulesAsync(Guid clientId, IReadOnlyList<string> moduleKeys, CancellationToken cancellationToken = default)
{
    UpdateResult result = await _clients.UpdateOneAsync(
        c => c.Id == clientId,
        Builders<ClientRegistration>.Update.Set(c => c.EnabledModules, moduleKeys),
        cancellationToken: cancellationToken);
    return result.MatchedCount > 0;
}
```

- [ ] **Step 3c: Add the endpoint.** In `AdminEndpoints.cs`, register in the per-client group (after `MapGet(".../members", ListMembers)`):

```csharp
perClient.MapPut("/clients/{clientId:guid}/modules", SetClientModules);
```

And add the handler:

```csharp
private static async Task<IResult> SetClientModules(
    Guid clientId, SetClientModulesRequest request, ClaimsPrincipal user,
    IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
{
    if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminClient, actorFactory, control, cancellationToken))
        return Results.Forbid();

    if (await control.GetClientAsync(clientId, cancellationToken) is null)
        return Results.NotFound();

    IReadOnlyList<string> keys = request.ModuleKeys ?? [];
    // Validate against installed modules — an unknown key is a mistake (no module would ever be entitled),
    // not inert. A helpful 400 beats a silently useless entitlement.
    foreach (string key in keys)
        if (await control.GetModuleAsync(key, cancellationToken) is null)
            return Results.Problem($"Unknown module '{key}'.", statusCode: StatusCodes.Status400BadRequest);

    await control.SetClientModulesAsync(clientId, keys, cancellationToken);
    return Results.Ok(new ClientModulesResponse(clientId, keys));
}
```

- [ ] **Step 4: Run to verify pass.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter FullyQualifiedName~ClientModulesTests`
Expected: PASS (4/4).

- [ ] **Step 5: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/ Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs
git commit -m "feat(tenancy): PUT /admin/clients/{id}/modules entitlement setter"
```

---

## Task 3: Default-closed enforcement + fixture sweep (atomic)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ModuleAccessTests.cs`
- Sweep: `ApiFixture.cs` (done in Task 3a if not already), `ReceivablesHostFixture.cs`, `PayablesHostFixture.cs`, `PayrollHostFixture.cs`, `CashHostFixture.cs`, `ReconciliationHostFixture.cs`, `DocumentStoreFixture.cs`, `PayablesDocumentStoreFixture.cs`
- Test (E2E): a Receivables host test file (append one `[Fact]`)

**Interfaces:**
- Consumes: `ControlStore.GetClientAsync`, `ClientRegistration.EnabledModules`.
- Produces: `ModuleAccessDecision.NotEntitled` (new enum member, positioned after `NotOwner`, before `NotMember`).

> This is the atomic point: enabling the check breaks every module suite until the sweep lands, so decision + check + sweep + all module suites green happen in ONE commit.

- [ ] **Step 1: Write the failing unit tests.** Append to `ModuleAccessTests.cs`:

```csharp
    [Fact]
    public async Task A_client_not_entitled_to_the_module_is_denied()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        ModuleAccess access = new(control);

        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("invoicing"), "invoicing", client.UserId, client.ClientId);

        Assert.Equal(ModuleAccessDecision.NotEntitled, decision);
    }

    [Fact]
    public async Task An_entitled_client_passes_the_entitlement_gate()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(enabledModules: ["invoicing"]);
        ModuleAccess access = new(control);

        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("invoicing"), "invoicing", client.UserId, client.ClientId);

        Assert.Equal(ModuleAccessDecision.Allowed, decision);
    }
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter FullyQualifiedName~ModuleAccessTests`
Expected: FAIL — `NotEntitled` does not exist (compile error) / the entitled case would still be `Allowed` only after the check exists.

- [ ] **Step 3: Add the decision + check.** In `ModuleAccess.cs`, add `NotEntitled` to the enum between `NotOwner` and `NotMember`:

```csharp
public enum ModuleAccessDecision
{
    Allowed,
    Unregistered,
    Disabled,
    NotOwner,
    NotEntitled,
    NotMember,
    MissingCapability,
}
```

And insert the check in `AuthorizeAsync`, immediately after the `NotOwner` check and before the membership lookup:

```csharp
        if (caller.Key != targetNamespace)
            return ModuleAccessDecision.NotOwner;

        // Entitlement (default-closed): the module must be in the client's EnabledModules. An unknown client
        // (not in this firm's control DB) is entitled to nothing — this is also the cross-firm isolation floor.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is null || !client.EnabledModules.Contains(caller.Key))
            return ModuleAccessDecision.NotEntitled;

        Membership? membership = await control.GetMembershipAsync(userId, clientId, cancellationToken);
```

- [ ] **Step 4: Sweep the host fixtures.** For each host fixture, add a default module list constant and set `EnabledModules` on every `RegisterClientAsync` call in its seed helpers. Add an optional `IReadOnlyList<string>? enabledModules = null` param to the parameterless/role `SeedClientAsync` so a test can request an unentitled client.

In `ReceivablesHostFixture.cs` add the constant near the top of the class:

```csharp
    private static readonly IReadOnlyList<string> DefaultModules = ["receivables"];
```

Change `SeedClientAsync` to accept an override and pass modules:

```csharp
    public async Task<(Guid ClientId, HttpClient Http)> SeedClientAsync(IReadOnlyList<string>? enabledModules = null)
    {
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        ControlStore control = Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
            EnabledModules = enabledModules ?? DefaultModules,
        });
        await control.AddMembershipAsync(userId, clientId, LedgerRole.Controller);
        return (clientId, ClientFor(userId, "Acme Controller"));
    }
```

And in `SeedSodClientAsync`, add `EnabledModules = DefaultModules,` to its `ClientRegistration` initializer.

Repeat for the other host fixtures, using these `DefaultModules` values (a client entitled to more modules than a test uses is harmless; the multi-module host fixtures run coexistence tests that touch several):

- `PayablesHostFixture.cs`: `["payables"]` — apply to both `SeedClientAsync()`, `SeedClientAsync(LedgerRole role)`, and `SeedSodClientAsync`.
- `PayrollHostFixture.cs`: `["receivables", "payables", "payroll"]` — apply to `SeedClientAsync(LedgerRole role)` and `SeedSodClientAsync`.
- `CashHostFixture.cs`: `["receivables", "payables", "payroll", "cash"]` — apply to `SeedClientAsync(LedgerRole role)` and `SeedSodClientAsync`.
- `ReconciliationHostFixture.cs`: `["receivables", "payables", "payroll", "cash", "reconciliation"]` — apply to `SeedClientAsync(LedgerRole role)` and `SeedSodClientAsync`.

For each, add `private static readonly IReadOnlyList<string> DefaultModules = [ ... ];` and set `EnabledModules = DefaultModules,` (or `enabledModules ?? DefaultModules` where an override param is added) on every `ClientRegistration` in that fixture's seed helpers.

- [ ] **Step 5: Sweep the direct document-store fixtures.** In `DocumentStoreFixture.cs` (Receivables), add `EnabledModules = ["receivables"],` to the `ClientRegistration` initializer. In `PayablesDocumentStoreFixture.cs`, add `EnabledModules = ["payables"],`.

- [ ] **Step 6: Add the entitle-then-succeed E2E.** Append one `[Fact]` to an existing Receivables host test class (whichever uses `ReceivablesHostFixture` — e.g. the invoice-issue test file). It seeds an unentitled client, confirms a module call is refused 403, enables the module via the Task 2 setter, and confirms the same call now succeeds. Adapt the module endpoint URL/body to the existing test's first successful call:

```csharp
    [Fact]
    public async Task An_unentitled_client_is_403_until_receivables_is_enabled()
    {
        (Guid clientId, HttpClient http) = await Fixture.SeedClientAsync(enabledModules: []);
        // <seed the chart of accounts exactly as the other Receivables tests do before issuing>

        // A module write (e.g. create a customer) is refused while unentitled.
        HttpResponseMessage before = await http.PostAsJsonAsync($"/clients/{clientId}/receivables/customers", NewCustomerBody());
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        // Enable the module through the entitlement setter, then the same call succeeds.
        HttpClient admin = Fixture.ClientFor(Guid.NewGuid(), "Admin"); // dev token; add ("admin","true") the way AdminClient does
        // If the fixture lacks an admin helper, mint the token with the admin claim inline.
        HttpResponseMessage set = await admin.PutAsJsonAsync(
            $"/admin/clients/{clientId}/modules", new SetClientModulesRequest(["receivables"]));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        HttpResponseMessage after = await http.PostAsJsonAsync($"/clients/{clientId}/receivables/customers", NewCustomerBody());
        Assert.True(after.IsSuccessStatusCode, $"expected success, got {(int)after.StatusCode}");
    }
```

> Implementer note: `ReceivablesHostFixture.ClientFor` mints a token with NO claims, so it cannot carry `admin=true`. Either (a) add an `AdminClient()` helper to `ReceivablesHostFixture` mirroring `ApiFixture.AdminClient()` (mint a `DevTokenPayload` with a `DevClaim("admin","true")`), or (b) grant the seeded member `admin.client` and drive the setter as that member. Prefer (a) — one small helper. Match the exact customer-create URL/body to the sibling Receivables tests; if creating a customer is not the first module call in that suite, use whatever call is.

- [ ] **Step 7: Run every module + engine suite green.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests`
Then each module suite:
`dotnet test Modules/Receivables/Accounting101.Receivables.Tests`
`dotnet test Modules/Payables/Accounting101.Payables.Tests`
`dotnet test Modules/Payroll/Accounting101.Payroll.Tests`
`dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests`
`dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: all PASS. If any module test seeds a client through a path other than the swept helpers (e.g. registers its module *after* `SeedClientAsync`, so `ApiFixture`'s `ListModulesAsync` default sees nothing), fix that test: register the module before seeding, or pass `enabledModules: [...]` explicitly. If a Settlement or other cross-module suite seeds module-exercising clients, apply the same `EnabledModules` set there.

- [ ] **Step 8: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs Backend/Accounting101.Ledger.Api.Tests/ModuleAccessTests.cs Modules/ Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs
git commit -m "feat(tenancy): default-closed module entitlement enforcement + fixture sweep"
```

---

## Task 4: Usage meter (`GET /platform/usage`)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformUsageTests.cs` (create)

**Interfaces:**
- Consumes: `UsageResponse`, `FirmUsageResponse` (Task 1); `PlatformStore.ListFirmsAsync`, `IMongoClientFactory.GetAsync`, `ControlStore.ListClientsAsync`, `ClientStatus.Active`.

- [ ] **Step 1: Write the failing test.** Create `PlatformUsageTests.cs`. It provisions a firm via `/platform/firms`, seeds two clients in that firm's control DB with differing `EnabledModules`/`Status`, and asserts the tally. Use the existing platform-test patterns (`fixture.ClientFor(..., ("platform","true"))` for the operator token — mirror `PlatformFirmsTests`):

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformUsageTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private HttpClient Operator() => fixture.ClientFor(Guid.NewGuid(), "Operator", ("platform", "true"));

    [Fact]
    public async Task Usage_tallies_active_clients_and_enabled_modules_per_firm()
    {
        HttpClient op = Operator();
        HttpResponseMessage provisioned = await op.PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "Meter Firm" });
        FirmResponse firm = (await provisioned.Content.ReadFromJsonAsync<FirmResponse>())!;

        // Two active clients (one with receivables, one with receivables+payables) and one archived.
        ControlStore control = new(fixture.Mongo.GetDatabase(firm.ControlDatabase));
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = Guid.NewGuid(), Name = "C1", DatabaseName = "d1",
            Status = ClientStatus.Active, EnabledModules = ["receivables"],
        });
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = Guid.NewGuid(), Name = "C2", DatabaseName = "d2",
            Status = ClientStatus.Active, EnabledModules = ["receivables", "payables"],
        });
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = Guid.NewGuid(), Name = "C3", DatabaseName = "d3",
            Status = ClientStatus.Archived, EnabledModules = ["receivables"],
        });

        UsageResponse usage = (await op.GetFromJsonAsync<UsageResponse>("/platform/usage"))!;
        FirmUsageResponse f = usage.Firms.Single(x => x.FirmId == firm.Id);

        Assert.Equal(2, f.ActiveClients);
        Assert.Equal(2, f.ModuleClientCounts["receivables"]); // only the two active clients
        Assert.Equal(1, f.ModuleClientCounts["payables"]);
    }

    [Fact]
    public async Task Usage_requires_the_platform_claim()
    {
        HttpClient nonOperator = fixture.ClientFor(Guid.NewGuid(), "Nobody");
        HttpResponseMessage response = await nonOperator.GetAsync("/platform/usage");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter FullyQualifiedName~PlatformUsageTests`
Expected: FAIL — `/platform/usage` 404s (unmapped).

- [ ] **Step 3: Add the route + handler.** In `PlatformEndpoints.cs`, register in `MapPlatformEndpoints`:

```csharp
        platform.MapGet("/usage", GetUsage);
```

And add the handler (counts modules over ACTIVE clients only — archiving stops the meter, per `ClientRegistration.Status`):

```csharp
    private static async Task<IResult> GetUsage(
        PlatformStore platform, IMongoClientFactory factory, CancellationToken cancellationToken)
    {
        IReadOnlyList<FirmRegistration> firms = await platform.ListFirmsAsync(cancellationToken);
        List<FirmUsageResponse> result = [];
        foreach (FirmRegistration firm in firms)
        {
            IMongoClient mongo;
            try
            {
                mongo = await factory.GetAsync(firm.ClusterKey, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Cluster no longer registered — skip this firm rather than fail the whole meter.
                continue;
            }

            ControlStore control = new(mongo.GetDatabase(firm.ControlDatabase));
            IReadOnlyList<ClientRegistration> clients = await control.ListClientsAsync(cancellationToken);
            List<ClientRegistration> active = clients.Where(c => c.Status == ClientStatus.Active).ToList();

            Dictionary<string, int> counts = new();
            foreach (ClientRegistration c in active)
                foreach (string key in c.EnabledModules)
                    counts[key] = counts.GetValueOrDefault(key) + 1;

            result.Add(new FirmUsageResponse(firm.Id, firm.Name, active.Count, counts));
        }
        return Results.Ok(new UsageResponse(result));
    }
```

- [ ] **Step 4: Run to verify pass.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter FullyQualifiedName~PlatformUsageTests`
Expected: PASS (2/2).

- [ ] **Step 5: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/PlatformUsageTests.cs
git commit -m "feat(tenancy): GET /platform/usage meter (active clients + enabled-module counts)"
```

---

## Task 5: Whole-solution green + review prep

- [ ] **Step 1: Build and test the whole solution.**

Run: `dotnet test Accounting101.slnx`
Expected: all PASS (Phase 3a baseline was 851/851; this adds ~8 tests and touches fixtures — expect the new higher total, 0 failures).

- [ ] **Step 2: Whole-branch review.** Follow `superpowers:requesting-code-review`. Key things a reviewer must confirm: (a) enforcement really is default-closed (empty `EnabledModules` → 403) and covers BOTH the `ScopedDocumentStore` path and the ledger-post path; (b) the check is firm-scoped (uses the injected scoped `ControlStore`, not a cross-firm reach); (c) the raw ledger path is unaffected; (d) no fixture accidentally entitles via a real client seed that a negative test relies on; (e) the setter's admin gate matches sibling endpoints; (f) usage never opens a cross-firm client DB it shouldn't and never leaks connection strings.

- [ ] **Step 3: Update the in-progress ledger / memory** (`accounting101-multi-firm-tenancy.md`): mark Phase 3b SHIPPED with the merge SHA and final test count; note the resolved open questions (unknown module keys → 400; per-firm module registration on provisioning still deferred); flag the demo client backfill as the one production data step.

---

## Self-Review

**Spec coverage:**
- Entitlement setter (`PUT /admin/clients/{id}/modules`, idempotent, admin-gated, unknown-key 400) → Task 2. ✓
- Chokepoint default-closed enforcement (`NotEntitled`, 403, firm-scoped, raw path unaffected) → Task 3. ✓
- Fixture sweep (all module host + document-store fixtures) → Task 3, Steps 4-5. ✓
- Usage meter (`GET /platform/usage`, PlatformAdmin, per-firm active clients + module counts) → Task 4. ✓
- Contracts → Task 1. ✓
- Demo-client backfill → Task 5 Step 3 note (production data, not a code task; done via the setter after deploy). ✓

**Resolved open questions:** unknown module keys → validate & reject 400 (Task 2). Per-firm module registration on provisioning → deferred (single demo firm; `ModuleRegistrar` seeds default firm only) — unchanged from 3a.

**Type consistency:** `SetClientModulesAsync(Guid, IReadOnlyList<string>, CancellationToken) → Task<bool>`, `ModuleAccessDecision.NotEntitled`, `SetClientModulesRequest.ModuleKeys`, `FirmUsageResponse.ModuleClientCounts` used consistently across tasks. ✓
