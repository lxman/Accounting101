# Slice E — Backend Per-Module & Admin Enforcement — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make subledger `.read`/`.write` and per-client `admin.*` capabilities real server-side, so the API rejects cross-module and unauthorized writes rather than only hiding UI controls.

**Architecture:** All five subledger modules funnel their document-store and GL-posting operations through the single method `ModuleAccess.AuthorizeAsync`. Slice E adds a per-module capability check there (parameterized by a read/write access level), so every module endpoint is enforced without editing any of them. Per-client admin endpoints adopt the existing "deployment-admin claim OR `admin.*` capability" pattern; control-plane bootstrap endpoints (create/list clients) stay deployment-admin-only.

**Tech Stack:** C# / .NET 10, ASP.NET Core Minimal APIs, MongoDB (EphemeralMongo in tests), xUnit.

## Global Constraints

- .NET 10; latest NuGets; namespaces follow folder structure.
- Tests use EphemeralMongo via `Accounting101.TestSupport.SharedMongo` (one shared mongod; isolate by GUID db name). Never start/dispose a private runner.
- camelCase JSON; strict binding (unmapped members → 400) is already configured in `Program.cs`.
- Commit trailer on every commit: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Backend build must stay warning-clean and green at the end of every task.
- Enforcement decisions read capabilities **from the control DB per request** — never from a token claim.

---

## File Structure

- `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs` — add `CapabilityForModule(moduleKey, level)`.
- `Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs` — add `ModuleAccessLevel` enum, `MissingCapability` decision, `level` parameter + capability check.
- `Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs` — thread a read/write level through `EnterAsync` and `NextNumberAsync`.
- `Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs` — pass `Write` from the module-posting branch.
- `Accounting101.Host/Program.cs` — map `ModuleAccessDeniedException` → 403.
- `Backend/Accounting101.Ledger.Api/Endpoints/AdminAuthorization.cs` — **new** shared "deployment-admin OR capability" helper.
- `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` — split route group; gate per-client endpoints.
- `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs` — route its check through the shared helper.
- Tests: `ModuleAccessTests.cs`, `ModulePostingTests.cs`, new `ModuleCapabilityEnforcementTests.cs`, new `AdminCapabilityTests.cs`.

---

## Task 1: Module → capability mapping + access-level vocabulary

Pure additions — no signatures change yet, so the build stays green on their own.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs`

**Interfaces:**
- Produces: `enum ModuleAccessLevel { Read, Write }` (namespace `Accounting101.Ledger.Api.Control`); `ModuleAccessDecision.MissingCapability`; `string? Capabilities.CapabilityForModule(string moduleKey, ModuleAccessLevel level)` — returns the required capability, or `null` for a module key with no subledger area (membership-only fallback).

- [ ] **Step 1: Add the `ModuleAccessLevel` enum and `MissingCapability` decision**

In `Control/ModuleAccess.cs`, add the enum above the `ModuleAccessDecision` enum, and add `MissingCapability` to `ModuleAccessDecision`:

```csharp
/// <summary>Whether a module operation reads or mutates data — selects the .read vs .write capability.</summary>
public enum ModuleAccessLevel
{
    Read,
    Write,
}

/// <summary>
/// The reason a module call was allowed or refused. Every refusal maps to HTTP 403 at the boundary;
/// the distinct reasons exist for logging and tests.
/// </summary>
public enum ModuleAccessDecision
{
    Allowed,
    Unregistered,
    Disabled,
    NotOwner,
    NotMember,
    MissingCapability,
}
```

- [ ] **Step 2: Write the failing test for `CapabilityForModule`**

In `Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs`, add:

```csharp
[Theory]
[InlineData("receivables", ModuleAccessLevel.Write, "ar.write")]
[InlineData("receivables", ModuleAccessLevel.Read, "ar.read")]
[InlineData("payables", ModuleAccessLevel.Write, "ap.write")]
[InlineData("payables", ModuleAccessLevel.Read, "ap.read")]
[InlineData("payroll", ModuleAccessLevel.Write, "payroll.write")]
[InlineData("payroll", ModuleAccessLevel.Read, "payroll.read")]
[InlineData("cash", ModuleAccessLevel.Write, "cash.write")]
[InlineData("cash", ModuleAccessLevel.Read, "cash.read")]
[InlineData("reconciliation", ModuleAccessLevel.Write, "bankrec.write")]
[InlineData("reconciliation", ModuleAccessLevel.Read, "bankrec.read")]
public void CapabilityForModule_maps_each_module_and_level(string key, ModuleAccessLevel level, string expected) =>
    Assert.Equal(expected, Capabilities.CapabilityForModule(key, level));

[Fact]
public void CapabilityForModule_returns_null_for_an_unmapped_module_key()
{
    Assert.Null(Capabilities.CapabilityForModule("invoicing", ModuleAccessLevel.Write));
    Assert.Null(Capabilities.CapabilityForModule("ghost", ModuleAccessLevel.Read));
}
```

Ensure the test file has `using Accounting101.Ledger.Api.Control;` (add if missing).

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~CapabilitiesTests.CapabilityForModule"`
Expected: FAIL — compile error, `CapabilityForModule` does not exist.

- [ ] **Step 4: Implement `CapabilityForModule`**

In `Control/Capabilities.cs`, add after `PermissionForCapability`:

```csharp
/// <summary>
/// The subledger capability a module requires for a given access level, or null when the module key
/// has no subledger area (a non-subledger module — falls back to membership-only authorization).
/// </summary>
public static string? CapabilityForModule(string moduleKey, ModuleAccessLevel level) => moduleKey switch
{
    "receivables"    => level == ModuleAccessLevel.Write ? ArWrite : ArRead,
    "payables"       => level == ModuleAccessLevel.Write ? ApWrite : ApRead,
    "payroll"        => level == ModuleAccessLevel.Write ? PayrollWrite : PayrollRead,
    "cash"           => level == ModuleAccessLevel.Write ? CashWrite : CashRead,
    "reconciliation" => level == ModuleAccessLevel.Write ? BankRecWrite : BankRecRead,
    _ => null,
};
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~CapabilitiesTests.CapabilityForModule"`
Expected: PASS (all InlineData cases + the null case).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs Backend/Accounting101.Ledger.Api/Control/Capabilities.cs Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs
git commit -m "$(printf 'feat(nav): module->capability map + access-level vocabulary (Slice E)\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>')"
```

---

## Task 2: Enforce the per-module capability at the chokepoint

Atomic change: `AuthorizeAsync` gains a `level` parameter and the capability check; its two production callers thread the correct level; the host maps `ModuleAccessDeniedException` → 403. These must land together to keep the build green.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs`
- Modify: `Accounting101.Host/Program.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleAccessTests.cs`
- Test (new): `Backend/Accounting101.Ledger.Api.Tests/ModuleCapabilityEnforcementTests.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModulePostingTests.cs`

**Interfaces:**
- Consumes: `ModuleAccessLevel`, `ModuleAccessDecision.MissingCapability`, `Capabilities.CapabilityForModule` (Task 1); `ControlStore.GetMembershipAsync` (returns `Membership?` with `.Capabilities`); `Membership.Capabilities` (`IReadOnlyList<string>`).
- Produces: `ModuleAccess.AuthorizeAsync(caller, targetNamespace, userId, clientId, level = Write, ct = default)`.

- [ ] **Step 1: Write the failing unit tests for the capability gate**

In `ModuleAccessTests.cs`, keep the existing `SeedAsync` helper and its five tests as-is; add a new role-parameterized helper that registers the mapped `"receivables"` module, plus the capability cases below:

```csharp
private async Task<(ModuleAccess access, Guid userId, Guid clientId)> SeedReceivablesAsync(LedgerRole role)
{
    ControlStore control = fixture.Control();
    await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
    SeededClient client = await fixture.SeedClientAsync(role: role);
    return (new ModuleAccess(control), client.UserId, client.ClientId);
}

[Fact]
public async Task A_member_holding_ar_write_may_write_receivables()
{
    (ModuleAccess access, Guid userId, Guid clientId) = await SeedReceivablesAsync(LedgerRole.Controller);
    ModuleAccessDecision decision = await access.AuthorizeAsync(
        new ModuleIdentity("receivables"), "receivables", userId, clientId, ModuleAccessLevel.Write);
    Assert.Equal(ModuleAccessDecision.Allowed, decision);
}

[Fact]
public async Task A_member_without_ar_write_is_denied_a_receivables_write()
{
    (ModuleAccess access, Guid userId, Guid clientId) = await SeedReceivablesAsync(LedgerRole.Auditor);
    ModuleAccessDecision decision = await access.AuthorizeAsync(
        new ModuleIdentity("receivables"), "receivables", userId, clientId, ModuleAccessLevel.Write);
    Assert.Equal(ModuleAccessDecision.MissingCapability, decision);
}

[Fact]
public async Task An_auditor_may_read_receivables()
{
    (ModuleAccess access, Guid userId, Guid clientId) = await SeedReceivablesAsync(LedgerRole.Auditor);
    ModuleAccessDecision decision = await access.AuthorizeAsync(
        new ModuleIdentity("receivables"), "receivables", userId, clientId, ModuleAccessLevel.Read);
    Assert.Equal(ModuleAccessDecision.Allowed, decision);
}

[Fact]
public async Task A_wrong_module_clerk_cannot_write_another_module()
{
    // ArClerk holds ar.write but NOT ap.write.
    ControlStore control = fixture.Control();
    await control.RegisterModuleAsync(new ModuleRegistration { Key = "payables", Name = "Payables", Enabled = true });
    SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.ArClerk);
    ModuleAccess access = new(control);
    ModuleAccessDecision decision = await access.AuthorizeAsync(
        new ModuleIdentity("payables"), "payables", client.UserId, client.ClientId, ModuleAccessLevel.Write);
    Assert.Equal(ModuleAccessDecision.MissingCapability, decision);
}
```

Leave the five existing `ModuleAccessTests` (they use the unmapped `"invoicing"` key → membership-only) unchanged; they must keep passing.

- [ ] **Step 2: Run to verify the new tests fail**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~ModuleAccessTests"`
Expected: FAIL — compile error (`AuthorizeAsync` has no `level` parameter).

- [ ] **Step 3: Add the `level` parameter + capability check to `AuthorizeAsync`**

In `Control/ModuleAccess.cs`, replace the method body:

```csharp
public async Task<ModuleAccessDecision> AuthorizeAsync(
    ModuleIdentity caller,
    string targetNamespace,
    Guid userId,
    Guid clientId,
    ModuleAccessLevel level = ModuleAccessLevel.Write,
    CancellationToken cancellationToken = default)
{
    ModuleRegistration? module = await control.GetModuleAsync(caller.Key, cancellationToken);
    if (module is null)
        return ModuleAccessDecision.Unregistered;
    if (!module.Enabled)
        return ModuleAccessDecision.Disabled;

    // Ownership is derived from identity (the store imposes target = caller.Key); the explicit
    // check makes any future named-target path safe too.
    if (caller.Key != targetNamespace)
        return ModuleAccessDecision.NotOwner;

    Membership? membership = await control.GetMembershipAsync(userId, clientId, cancellationToken);
    if (membership is null)
        return ModuleAccessDecision.NotMember;

    // A mapped subledger module requires the acting user to hold its .read/.write capability. An
    // unmapped module key (no subledger area) falls back to membership-only, its historical behavior.
    string? required = Capabilities.CapabilityForModule(caller.Key, level);
    if (required is not null && !membership.Capabilities.Contains(required))
        return ModuleAccessDecision.MissingCapability;

    return ModuleAccessDecision.Allowed;
}
```

Update the class XML summary's "AND the acting user must be a member of the client" clause to note it now also checks the per-module capability.

- [ ] **Step 4: Thread the level through `ScopedDocumentStore`**

In `Documents/ScopedDocumentStore.cs`:

1. Change `EnterAsync`'s signature and the `AuthorizeAsync` call:

```csharp
private async Task<Ctx> EnterAsync(Guid clientId, string collection, ModuleAccessLevel level, CancellationToken cancellationToken)
{
    Actor actor = currentActor.Get();
    ModuleAccessDecision decision = await access.AuthorizeAsync(identity, identity.Key, actor.UserId, clientId, level, cancellationToken);
    if (decision != ModuleAccessDecision.Allowed)
        throw new ModuleAccessDeniedException(identity.Key, collection, decision);
    // ... unchanged remainder (resolver, physical, EnsureIndexesAsync, return) ...
}
```

2. Update every `EnterAsync` call to pass its intent:
   - **Read** (`ModuleAccessLevel.Read`): `GetAsync` (line ~53), `QueryAsync` (line ~66), `CountAsync` (line ~75).
   - **Write** (`ModuleAccessLevel.Write`): `PutAsync` (line ~38), `DeleteAsync` (line ~83), `DeactivateAsync` (line ~92), `CreateAsync` (line ~109), `UpdateAsync` (line ~120), `FinalizeAsync` (line ~132), `SupersedeAsync` (line ~165), `VoidAsync` (line ~204).

   Example (write): `Ctx ctx = await EnterAsync(clientId, collection, ModuleAccessLevel.Write, cancellationToken);`
   Example (read): `Ctx ctx = await EnterAsync(clientId, collection, ModuleAccessLevel.Read, cancellationToken);`

3. In `NextNumberAsync` (line ~214), pass `Write` to the direct `AuthorizeAsync` call:

```csharp
ModuleAccessDecision decision = await access.AuthorizeAsync(identity, identity.Key, actor.UserId, clientId, ModuleAccessLevel.Write, cancellationToken);
```

- [ ] **Step 5: Thread the level through `LedgerGateway.ResolveForPostAsync`**

In `Endpoints/LedgerGateway.cs`, the module branch call becomes a write:

```csharp
ModuleAccessDecision decision = await moduleAccess.AuthorizeAsync(
    module, module.Key, actor.UserId, clientId, ModuleAccessLevel.Write, cancellationToken);
```

- [ ] **Step 6: Map `ModuleAccessDeniedException` → 403 in the host**

In `Accounting101.Host/Program.cs`, extend the existing `app.Use(...)` middleware (the one already catching `JsonException`) with a catch for the access exception, so every module endpoint returns 403 uniformly. Add `catch` before the `JsonException` catches:

```csharp
catch (Accounting101.Ledger.Api.Documents.ModuleAccessDeniedException ex)
{
    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
    await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
    {
        Status = StatusCodes.Status403Forbidden,
        Title  = "Forbidden",
        Detail = ex.Message,
    }, ctx.RequestAborted);
}
```

- [ ] **Step 7: Run the unit tests — they now pass**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~ModuleAccessTests"`
Expected: PASS (new capability cases + the five unchanged membership cases).

- [ ] **Step 8: Write the document-store + HTTP enforcement tests**

Create `Backend/Accounting101.Ledger.Api.Tests/ModuleCapabilityEnforcementTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Slice E: the document store and the receivables HTTP surface enforce ar.read/ar.write. An auditor
/// (ar.read, no ar.write) may read but not write; the write refusal surfaces as HTTP 403, not 500.
/// </summary>
public sealed class ModuleCapabilityEnforcementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly ModuleIdentity Receivables = new("receivables");

    private static ModuleManifest Manifest() =>
        new ModuleManifestBuilder().Reference("customers").Build();

    private sealed class FixedActor(Guid userId) : ICurrentActor
    {
        public Actor Get() => new() { UserId = userId, Name = "Tester" };
    }

    public sealed record Party(string Name);

    private ScopedDocumentStore StoreFor(Guid userId, ControlStore control) =>
        new(Receivables, Manifest(), new ClientDatabaseResolver(fixture.Mongo, control), new FixedActor(userId), new ModuleAccess(control));

    [Fact]
    public async Task Auditor_write_to_the_document_store_is_denied_but_read_is_allowed()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Auditor);
        ScopedDocumentStore store = StoreFor(client.UserId, control);

        await Assert.ThrowsAsync<ModuleAccessDeniedException>(() =>
            store.PutAsync(client.ClientId, "customers", Guid.NewGuid(), new Party("Acme"), new Dictionary<string, string>()));

        // Read must NOT throw (auditor holds ar.read).
        IReadOnlyList<DocumentResult<Party>> hits =
            await store.QueryAsync<Party>(client.ClientId, "customers", new Dictionary<string, string>());
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Auditor_creating_a_customer_over_http_gets_403()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Auditor);

        HttpResponseMessage resp = await client.Http.PostAsJsonAsync(
            $"/clients/{client.ClientId}/customers", new CreateCustomerRequest("Acme", "acme@example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Controller_creating_a_customer_over_http_succeeds()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Controller);

        HttpResponseMessage resp = await client.Http.PostAsJsonAsync(
            $"/clients/{client.ClientId}/customers", new CreateCustomerRequest("Acme", "acme@example.com"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
```

Note: confirm the exact `CreateCustomerRequest` shape in `Accounting101.Ledger.Contracts` (it is used by `ReceivablesEndpoints.CreateCustomer` as `request.Name`, `request.Email`); adjust the constructor call to match its record definition if the parameter order/names differ.

- [ ] **Step 9: Migrate `ModulePostingTests` to the capability model**

The first test posts as an **Approver** via `payables` expecting 201 — but Approver holds no `ap.write`, so Slice E now refuses it. Switch the module-authorized poster to a **Clerk** (holds all subledger `.write`, but no `gl.post` — so the raw path still 403s, preserving the test's point). Apply to all three tests that add an Approver member and post via the `payables` module:

- `Module_credential_allows_approver_to_post_and_stamps_ViaModule`
- `Approver_without_module_headers_gets_403`
- `Actor_is_always_the_token_bearer_even_with_module_headers`

In each, replace:

```csharp
Guid approverId = Guid.NewGuid();
await fixture.Control().AddMembershipAsync(approverId, c.ClientId, LedgerRole.Approver);
HttpClient approverHttp = fixture.ClientFor(approverId, "Approver");
```

with:

```csharp
Guid clerkId = Guid.NewGuid();
await fixture.Control().AddMembershipAsync(clerkId, c.ClientId, LedgerRole.Clerk);
HttpClient clerkHttp = fixture.ClientFor(clerkId, "Clerk");
```

and rename the local `approverHttp`/`approverId` usages accordingly (including the `first.Actor.UserId` assertion → `clerkId`). Rename the two affected test methods to `Module_credential_allows_clerk_to_post_...` and `Clerk_without_module_headers_gets_403` for accuracy; update their XML docs to say "a Clerk holds ap.write but not gl.post."

The `Disabled_module_credentials_are_refused_with_403` test uses a disabled/unmapped key (denied before the capability check) — leave its member as-is or switch to Clerk for consistency; either passes. `Raw_clerk_post_succeeds_and_ViaModule_is_null` uses the default-Controller seed and is unaffected.

- [ ] **Step 10: Add the cross-module rejection test to `ModulePostingTests`**

Add a test proving a member who holds a *different* module's write cannot post through `payables`:

```csharp
/// <summary>
/// An ArClerk holds ar.write but not ap.write. Even with a valid payables module credential, the
/// per-module capability check refuses the post — cross-module writes are rejected server-side.
/// </summary>
[Fact]
public async Task Wrong_module_clerk_cannot_post_via_another_modules_credential()
{
    SeededClient c = await fixture.SeedClientAsync("CrossModule");

    Guid arClerkId = Guid.NewGuid();
    await fixture.Control().AddMembershipAsync(arClerkId, c.ClientId, LedgerRole.ArClerk);
    HttpClient arClerkHttp = fixture.ClientFor(arClerkId, "ArClerk");

    await fixture.Control().RegisterModuleAsync(new ModuleRegistration
    {
        Key = ModuleKey, Name = "Payables", Enabled = true, Secret = ModuleSecret,
    });

    HttpResponseMessage resp = await PostWithModuleAsync(arClerkHttp, c.ClientId, BalancedEntry(), ModuleKey, ModuleSecret);

    Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
}
```

- [ ] **Step 11: Run the full API test project**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests`
Expected: PASS — all suites, including migrated `ModulePostingTests`, new enforcement tests, and unchanged `DocumentStore*Tests` (which use the unmapped `"invoicing"` key → membership-only).

- [ ] **Step 12: Run the five module test projects (regression)**

Run: `cd Backend && dotnet test ../Modules/Receivables/Accounting101.Receivables.Tests && dotnet test ../Modules/Payables/Accounting101.Payables.Tests && dotnet test ../Modules/Payroll/Accounting101.Payroll.Tests && dotnet test ../Modules/Banking/Cash/Accounting101.Banking.Cash.Tests && dotnet test ../Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: PASS — every module document-store fixture seeds a `Controller` member (holds all subledger `.write`/`.read`), so enforcement is transparent to them.

- [ ] **Step 13: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs Accounting101.Host/Program.cs Backend/Accounting101.Ledger.Api.Tests/ModuleAccessTests.cs Backend/Accounting101.Ledger.Api.Tests/ModuleCapabilityEnforcementTests.cs Backend/Accounting101.Ledger.Api.Tests/ModulePostingTests.cs
git commit -m "$(printf 'feat(nav): enforce per-module subledger capabilities at the module chokepoint (Slice E)\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>')"
```

---

## Task 3: Per-client admin capability gates

Route per-client admin endpoints through a shared "deployment-admin OR `admin.*` capability" check; keep control-plane client provisioning deployment-admin-only.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/AdminAuthorization.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`
- Test (new): `Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs`

**Interfaces:**
- Produces: `AdminAuthorization.MayAsync(ClaimsPrincipal user, Guid clientId, string capability, IActorFactory actorFactory, ControlStore control, CancellationToken ct) : Task<bool>`.
- Consumes: `ControlStore.GetMembershipAsync`, `ControlStore.SetMembershipAsync`, `Capabilities.AdminFiscal`, `Capabilities.AdminUsers`.

- [ ] **Step 1: Create the shared authorization helper**

Create `Backend/Accounting101.Ledger.Api/Endpoints/AdminAuthorization.cs`:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// The gate for per-client administrative endpoints: a deployment admin (trusted <c>admin=true</c>
/// token claim) may always act; otherwise the acting user must be a member of the client holding the
/// required <c>admin.*</c> capability. Control-plane provisioning (create/list clients) has no
/// per-client context and stays deployment-admin-only via the endpoint policy.
/// </summary>
internal static class AdminAuthorization
{
    public static async Task<bool> MayAsync(
        ClaimsPrincipal user, Guid clientId, string capability,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (user.HasClaim("admin", "true"))
            return true;
        Actor actor = actorFactory.Create(user);
        Membership? m = await control.GetMembershipAsync(actor.UserId, clientId, ct);
        return m is not null && m.Capabilities.Contains(capability);
    }
}
```

- [ ] **Step 2: Write the failing admin tests**

Create `Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Slice E: per-client admin endpoints accept a deployment admin OR a member holding the matching
/// admin.* capability, and refuse a member without it. Control-plane client provisioning stays
/// deployment-admin-only.
/// </summary>
public sealed class AdminCapabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http)> MemberWithAsync(params string[] capabilities)
    {
        SeededClient c = await fixture.SeedClientAsync("AdminCaps");
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], capabilities);
        return (c.ClientId, fixture.ClientFor(userId, "Member"));
    }

    [Fact]
    public async Task Member_with_admin_fiscal_may_set_fiscal_year_end()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminFiscal);
        HttpResponseMessage resp = await http.PutAsJsonAsync(
            $"/admin/clients/{clientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_without_admin_fiscal_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.PutAsJsonAsync(
            $"/admin/clients/{clientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_set_fiscal_year_end()
    {
        SeededClient c = await fixture.SeedClientAsync("AdminCapsDeploy");
        HttpResponseMessage resp = await fixture.AdminClient().PutAsJsonAsync(
            $"/admin/clients/{c.ClientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_with_admin_users_may_list_members()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminUsers);
        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/members");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_without_admin_users_is_forbidden_from_listing_members()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/members");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Creating_a_client_still_requires_a_deployment_admin()
    {
        SeededClient c = await fixture.SeedClientAsync("NotDeployAdmin");
        HttpResponseMessage resp = await c.Http.PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest("New Co", null, false, 12));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
```

Confirm the exact `CreateClientRequest` / `SetFiscalYearEndRequest` shapes in `Accounting101.Ledger.Contracts` and adjust the constructor calls to match (parameter order/names). `SetFiscalYearEndRequest` carries `FiscalYearEndMonth`; `CreateClientRequest` carries `Name`, `DatabaseName?`, `RequireSegregationOfDuties`, `FiscalYearEndMonth`.

- [ ] **Step 3: Run to verify the fiscal/members tests fail**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AdminCapabilityTests"`
Expected: FAIL — the per-client endpoints currently require the `DeploymentAdmin` policy, so a plain member gets 403 even *with* the capability (the "may set / may list" tests fail); `Creating_a_client...` may already pass.

- [ ] **Step 4: Split the AdminEndpoints route group and gate per-client handlers**

In `Endpoints/AdminEndpoints.cs`:

1. Add usings at the top:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
```

2. Replace `MapAdminEndpoints`:

```csharp
public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
{
    // Control-plane: deployment admin only — no per-client context exists to gate on.
    RouteGroupBuilder deployment = app.MapGroup("/admin").RequireAuthorization(Policy);
    deployment.MapPost("/clients", CreateClient);
    deployment.MapGet("/clients", ListClients);

    // Per-client admin: deployment admin OR the matching admin.* capability (checked in-handler).
    RouteGroupBuilder perClient = app.MapGroup("/admin").RequireAuthorization();
    perClient.MapPut("/clients/{clientId:guid}/fiscal-year-end", SetFiscalYearEnd);
    perClient.MapPost("/clients/{clientId:guid}/members", AddMember);
    perClient.MapGet("/clients/{clientId:guid}/members", ListMembers);
}
```

3. Add the capability check + injected `ClaimsPrincipal`/`IActorFactory` to the three per-client handlers. `SetFiscalYearEnd`:

```csharp
private static async Task<IResult> SetFiscalYearEnd(
    Guid clientId, SetFiscalYearEndRequest request, ClaimsPrincipal user,
    IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
{
    if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminFiscal, actorFactory, control, cancellationToken))
        return Results.Forbid();

    if (request.FiscalYearEndMonth is < 1 or > 12)
        return Results.Problem("FiscalYearEndMonth must be between 1 and 12.", statusCode: StatusCodes.Status400BadRequest);
    // ... unchanged remainder ...
}
```

`AddMember` and `ListMembers`: add the same `ClaimsPrincipal user, IActorFactory actorFactory` parameters and, as the first line of each body,

```csharp
if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminUsers, actorFactory, control, cancellationToken))
    return Results.Forbid();
```

- [ ] **Step 5: Route MemberEndpoints through the shared helper**

In `Endpoints/MemberEndpoints.cs`, replace the body of `CallerMayManage` to delegate (keeping its signature so its callers are untouched):

```csharp
private static Task<bool> CallerMayManage(ClaimsPrincipal user, Guid clientId, IActorFactory actorFactory, ControlStore control, CancellationToken ct) =>
    AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminUsers, actorFactory, control, ct);
```

- [ ] **Step 6: Run the admin tests — they pass**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AdminCapabilityTests"`
Expected: PASS.

- [ ] **Step 7: Run the full API test project (regression)**

Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests`
Expected: PASS — including any existing admin/member tests (deployment admin still satisfies `MayAsync` via the `admin=true` claim).

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/AdminAuthorization.cs Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs
git commit -m "$(printf 'feat(nav): per-client admin.* capability gates on fiscal + member endpoints (Slice E)\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>')"
```

---

## Final verification

- [ ] **Full backend suite green:** `cd Backend && dotnet test` (API + control + contracts + core + mongo).
- [ ] **All five module suites green** (Task 2, Step 12).
- [ ] **Warning-clean build:** `cd Backend && dotnet build -warnaserror` (or confirm 0 warnings).
- [ ] **Spec cross-check:** subledger read+write enforced at the chokepoint (Task 2); admin fiscal + members gated, create/list stay deployment-only (Task 3); `ModuleAccessDeniedException` → 403 (Task 2, Step 6); no UI change.

## Notes for the implementer

- The enforcement lives in ONE method (`ModuleAccess.AuthorizeAsync`). Do not add per-endpoint capability checks in the module `*Endpoints.cs` files — that is deliberately avoided.
- The unmapped-key fallback (`CapabilityForModule` → null → membership-only) is why the generic `DocumentStore*Tests` (module key `"invoicing"`) keep passing without a subledger capability. Do not "fix" them to require a capability.
- Role capability reference (from `RolePresets`): Controller/Clerk/Admin hold all subledger `.write`; Auditor and Approver hold none; ArClerk holds `ar.write` only; every role holds every `*.read`; only Admin holds `admin.*`.
- If a `*Request` record's constructor in a test does not match the contract, fix the test call to the real shape — do not change the contract.
