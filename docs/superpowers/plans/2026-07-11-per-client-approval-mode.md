# Per-Client Approval Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-client `RequireSegregationOfDuties` boolean with a single `ApprovalMode { TwoPerson, SelfApprove, AutoApprove }` enum, add auto-approve enforcement across every entry-creation path, gate the setting on a new `admin.approvalPolicy` capability, expose a read/set API + admin screen, and remove the unused `admin.firm` ghost capability.

**Architecture:** The approval posture is host policy that lives in the control DB (`ClientRegistration`), never in the engine. A normalizer (`ApprovalPolicy.ModeOf`, mirroring `FiscalYear.MonthOf`) resolves the effective mode from the stored enum, falling back to the legacy bool for pre-existing documents (lazy migration, no backfill). Enforcement stays in the host: the existing SoD guard reroutes to `ModeOf == TwoPerson`, and AutoApprove is applied at the four host entry-creation handlers by reusing the engine's `ApproveAsync`. The enum serializes as a string on the wire via a per-type `[JsonConverter]` (there is no global converter in this repo).

**Tech Stack:** C# 12 / .NET 8 minimal APIs, MongoDB (`MongoDB.Driver`), xUnit + `EphemeralMongo` for backend; Angular (standalone, OnPush, signals), Spartan `HlmButton`, Vitest + `HttpTestingController` for frontend.

## Global Constraints

- **Commit trailer** (every commit body): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- **Branch:** `feat/per-client-approval-mode` (already created; the design spec is already committed there). Commit after each task. **Do not push.**
- **Enums on the wire** get a **per-type** `[JsonConverter(typeof(JsonStringEnumConverter))]` — there is **no** global `JsonStringEnumConverter`. A serialization test must prove string output, not a number.
- **Mongo enum storage** uses `[BsonRepresentation(BsonType.String)]` (as `ClientRegistration.Status` does).
- **`ApprovalMode` enum lives in `Accounting101.Ledger.Contracts`** (wire vocabulary); the `ApprovalPolicy` normalizer lives in `Accounting101.Ledger.Api.Control`.
- **Backend test command:** `dotnet test Accounting101.slnx -m:1`
- **Frontend test command (scoped):** `ng test --include='**/<file>.spec.ts' --watch=false`. The full `ng test` exits 1 on a **pre-existing** NG04002 router flake unrelated to this work — scope to the touched specs.
- `UI/Angular/src/app/core/api/environment.ts` stays **uncommitted** (do not stage it).
- **Semantics of the three modes:** `TwoPerson` = author ≠ approver; `SelfApprove` = author may approve own; `AutoApprove` = the host approves each just-created pending entry inline with the creating actor (writing both `Created` and `Approved` audit events), across single post, batch post, revise, and reverse. Subledger modules post through the shared post handlers, so they are covered transitively with no per-module code.

---

## File Structure

**Backend — create:**
- `Backend/Accounting101.Ledger.Contracts/ApprovalMode.cs` — the enum (wire vocabulary).
- `Backend/Accounting101.Ledger.Api/Control/ApprovalPolicy.cs` — `ModeOf` normalizer.
- `Backend/Accounting101.Ledger.Api/Endpoints/ApprovalPolicyEndpoints.cs` — GET/PUT `/clients/{id}/approval-policy`.
- `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyTests.cs` — normalizer + string-serialization unit tests.
- `Backend/Accounting101.Ledger.Api.Tests/ApprovalCapabilityTests.cs` — cap vocabulary, preset, seeded set, ghost removal.
- `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyEndpointTests.cs` — GET/PUT authorization + behavior.
- `Backend/Accounting101.Ledger.Api.Tests/ApprovalModeEnforcementTests.cs` — TwoPerson/SelfApprove/AutoApprove across the four creation paths.

**Backend — modify:**
- `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs` — add `ApprovalMode` field; re-comment the legacy bool.
- `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs` — add `AdminApprovalPolicy`, remove `AdminFirm` (const + `All`).
- `Backend/Accounting101.Ledger.Api/Control/RolePresets.cs` — Admin preset: add approval-policy, remove firm.
- `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs` — seed the "Approval Policy Admin" narrow set.
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — swap DTO fields; add approval-policy DTOs.
- `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` — create/get/list use `ApprovalMode`.
- `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` — guard reroute + AutoApprove hooks + helpers.
- `Accounting101.Host/Program.cs` — map the new endpoints.
- `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs` — `SeedClientAsync` gains an `approvalMode` param.
- `Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs`, `AdminTests.cs`, `ClientRegistrationTenancyFieldsTests.cs` — update DTO/BSON usages of the dropped bool.

**Frontend — create:**
- `UI/Angular/src/app/core/approval-policy/approval-policy.ts` — types.
- `UI/Angular/src/app/core/approval-policy/approval-policy.service.ts` (+ `.spec.ts`) — GET/PUT service.
- `UI/Angular/src/app/features/admin/approval-policy.ts` (+ `.spec.ts`) — the radio-group screen.

**Frontend — modify:**
- `UI/Angular/src/app/app.routes.ts` — route (guarded).
- `UI/Angular/src/app/layout/nav.ts` — Administration nav entry.

---

## Task 1: Data model — `ApprovalMode` enum, `ApprovalPolicy` normalizer, storage field

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/ApprovalMode.cs`
- Create: `Backend/Accounting101.Ledger.Api/Control/ApprovalPolicy.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs:26-31`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs:69-96`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyTests.cs`

**Interfaces:**
- Produces: `enum ApprovalMode { Unspecified = 0, TwoPerson = 1, SelfApprove = 2, AutoApprove = 3 }` (namespace `Accounting101.Ledger.Contracts`, carries `[JsonConverter(typeof(JsonStringEnumConverter))]`); `ClientRegistration.ApprovalMode` (get/set, `[BsonRepresentation(BsonType.String)]`); `static ApprovalMode ApprovalPolicy.ModeOf(ClientRegistration)`; `ApiFixture.SeedClientAsync(..., ApprovalMode approvalMode = ApprovalMode.Unspecified)`.

- [ ] **Step 1: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyTests.cs`:

```csharp
using System.Text.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalPolicyTests
{
    [Fact]
    public void Stored_mode_wins_over_legacy_bool()
    {
        ClientRegistration c = new() { ApprovalMode = ApprovalMode.AutoApprove, RequireSegregationOfDuties = true };
        Assert.Equal(ApprovalMode.AutoApprove, ApprovalPolicy.ModeOf(c));
    }

    [Fact]
    public void Legacy_true_normalizes_to_two_person()
    {
        ClientRegistration c = new() { ApprovalMode = ApprovalMode.Unspecified, RequireSegregationOfDuties = true };
        Assert.Equal(ApprovalMode.TwoPerson, ApprovalPolicy.ModeOf(c));
    }

    [Fact]
    public void Legacy_false_normalizes_to_self_approve()
    {
        ClientRegistration c = new() { ApprovalMode = ApprovalMode.Unspecified, RequireSegregationOfDuties = false };
        Assert.Equal(ApprovalMode.SelfApprove, ApprovalPolicy.ModeOf(c));
    }

    [Fact]
    public void Mode_serializes_as_a_string_not_a_number()
    {
        string json = JsonSerializer.Serialize(new { Mode = ApprovalMode.AutoApprove });
        Assert.Contains("\"AutoApprove\"", json);
        Assert.DoesNotContain("3", json);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: FAIL — `ApprovalMode` / `ApprovalPolicy` do not exist (compile error).

- [ ] **Step 3: Create the enum**

Create `Backend/Accounting101.Ledger.Contracts/ApprovalMode.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Accounting101.Ledger.Contracts;

/// <summary>
/// A client's approval posture (host policy, per client). One enum so the illegal combination —
/// segregation of duties on AND auto-approve on — is unrepresentable. <see cref="Unspecified"/> is a
/// legacy sentinel: a document stored before this field existed deserializes to it, and readers
/// normalize via <c>ApprovalPolicy.ModeOf</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalMode
{
    Unspecified = 0,
    TwoPerson = 1,
    SelfApprove = 2,
    AutoApprove = 3,
}
```

- [ ] **Step 4: Add the storage field and re-comment the legacy bool**

In `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs`, add the Contracts using at the top (after the existing `using` lines):

```csharp
using Accounting101.Ledger.Contracts;
```

Replace the `RequireSegregationOfDuties` property (lines 21-26) with:

```csharp
    /// <summary>
    /// LEGACY. Superseded by <see cref="ApprovalMode"/>; retained only so documents written before the
    /// enum existed still deserialize. Never written going forward — <c>ApprovalPolicy.ModeOf</c> reads it
    /// only when <see cref="ApprovalMode"/> is <see cref="Contracts.ApprovalMode.Unspecified"/>.
    /// </summary>
    public bool RequireSegregationOfDuties { get; set; }

    /// <summary>The client's approval posture (two-person / self-approve / auto-approve). Host policy, stored
    /// here in the control DB, not in the engine. A legacy document with no value deserializes to
    /// <see cref="Contracts.ApprovalMode.Unspecified"/>; read the effective mode via <c>ApprovalPolicy.ModeOf</c>.</summary>
    [BsonRepresentation(BsonType.String)]
    public ApprovalMode ApprovalMode { get; set; }
```

- [ ] **Step 5: Create the normalizer**

Create `Backend/Accounting101.Ledger.Api/Control/ApprovalPolicy.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Control;

/// <summary>Approval-posture helper. Resolves the effective <see cref="ApprovalMode"/> for a client,
/// falling back to the legacy <see cref="ClientRegistration.RequireSegregationOfDuties"/> bool for
/// documents written before the enum existed (lazy migration — no backfill). Mirrors
/// <see cref="FiscalYear.MonthOf"/>.</summary>
public static class ApprovalPolicy
{
    public static ApprovalMode ModeOf(ClientRegistration client) =>
        client.ApprovalMode != ApprovalMode.Unspecified
            ? client.ApprovalMode
            : client.RequireSegregationOfDuties ? ApprovalMode.TwoPerson : ApprovalMode.SelfApprove;
}
```

- [ ] **Step 6: Add the `approvalMode` parameter to the test fixture**

In `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`, change the `SeedClientAsync` signature (line 69-71) to add the parameter, and set it on the registration.

Signature becomes:

```csharp
    public async Task<SeededClient> SeedClientAsync(
        string name = "Acme", bool requireSod = false, LedgerRole role = LedgerRole.Controller,
        IReadOnlyList<string>? enabledModules = null, ApprovalMode approvalMode = ApprovalMode.Unspecified)
```

In the `RegisterClientAsync(new ClientRegistration { ... })` initializer (line 85-92), add one line after `RequireSegregationOfDuties = requireSod,`:

```csharp
            ApprovalMode = approvalMode,
```

Add the using at the top of `ApiFixture.cs` if not present:

```csharp
using Accounting101.Ledger.Contracts;
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — all four `ApprovalPolicyTests` green; whole solution still compiles and passes (the storage class keeps the bool, so nothing else breaks yet).

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/ApprovalMode.cs \
        Backend/Accounting101.Ledger.Api/Control/ApprovalPolicy.cs \
        Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs \
        Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs \
        Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyTests.cs
git commit -m "feat(approval-mode): ApprovalMode enum, ModeOf normalizer, storage field

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: RBAC — `admin.approvalPolicy` capability, seeded narrow set, remove `admin.firm` ghost

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs:44,89-97`
- Modify: `Backend/Accounting101.Ledger.Api/Control/RolePresets.cs:35-42`
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs:328-333`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ApprovalCapabilityTests.cs`

**Interfaces:**
- Consumes: `Capabilities`, `RolePresets.For(LedgerRole)`, `ControlStore.ListCapabilitySetsAsync`, `ApiFixture.Control()`.
- Produces: `Capabilities.AdminApprovalPolicy = "admin.approvalPolicy"` (present in `Capabilities.All`); `Capabilities.AdminFirm` **removed**; the `Admin` preset contains `admin.approvalPolicy` and not `admin.firm`; a seeded built-in capability set named `"Approval Policy Admin"` = `[admin.approvalPolicy, gl.read]`.

- [ ] **Step 1: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/ApprovalCapabilityTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalCapabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public void Vocabulary_adds_approval_policy_and_drops_firm()
    {
        Assert.Contains("admin.approvalPolicy", Capabilities.All);
        Assert.DoesNotContain("admin.firm", Capabilities.All);
    }

    [Fact]
    public void Admin_preset_has_approval_policy_and_not_firm()
    {
        IReadOnlySet<string> admin = RolePresets.For(LedgerRole.Admin);
        Assert.Contains(Capabilities.AdminApprovalPolicy, admin);
        Assert.DoesNotContain("admin.firm", admin);
    }

    [Fact]
    public async Task Approval_policy_admin_narrow_set_is_seeded()
    {
        // Boot the host (mints a client) so seeding runs, then read the control DB's capability sets.
        await fixture.SeedClientAsync("SeedProbe");
        IReadOnlyList<CapabilitySet> sets = await fixture.Control().ListCapabilitySetsAsync();
        CapabilitySet? set = sets.FirstOrDefault(s => s.Name == "Approval Policy Admin");
        Assert.NotNull(set);
        Assert.Equal(new[] { Capabilities.AdminApprovalPolicy, Capabilities.GlRead }, set!.Capabilities);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: FAIL — `Capabilities.AdminApprovalPolicy` does not exist (compile error).

- [ ] **Step 3: Update the capability vocabulary**

In `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs`:

Replace the admin block (lines 43-47) — **remove `AdminFirm`, add `AdminApprovalPolicy`**:

```csharp
    // Admin.
    public const string AdminUsers = "admin.users";
    public const string AdminClient = "admin.client";
    public const string AdminFiscal = "admin.fiscal";
    public const string AdminPostingAccounts = "admin.postingAccounts";
    public const string AdminApprovalPolicy = "admin.approvalPolicy";
```

In the `All` set (line 96), replace the admin line — **remove `AdminFirm`, add `AdminApprovalPolicy`**:

```csharp
        AdminUsers, AdminClient, AdminFiscal, AdminPostingAccounts, AdminApprovalPolicy,
```

- [ ] **Step 4: Update the Admin role preset**

In `Backend/Accounting101.Ledger.Api/Control/RolePresets.cs`, replace the two admin-cap lines inside the `LedgerRole.Admin` preset (lines 40-41) — **remove `AdminFirm`, add `AdminApprovalPolicy`**:

```csharp
            Capabilities.AdminUsers, Capabilities.AdminClient,
            Capabilities.AdminFiscal, Capabilities.AdminPostingAccounts, Capabilities.AdminApprovalPolicy,
```

- [ ] **Step 5: Seed the narrow set**

In `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`, add one entry to the `narrowAdmins` array (after the "Posting-Accounts Admin" line, ~line 332):

```csharp
            ("Approval Policy Admin", [Capabilities.AdminApprovalPolicy, Capabilities.GlRead]),
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — the three new tests green. If any **other** test fails referencing `admin.firm`/`AdminFirm` or an exact `Capabilities.All` count, update it to match the new vocabulary (a repo grep `AdminFirm|admin.firm` under `Backend/**/*.cs` should now show only `RolePresets.cs`/`Capabilities.cs` gone and no test references — fix any that surface).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/Capabilities.cs \
        Backend/Accounting101.Ledger.Api/Control/RolePresets.cs \
        Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Api.Tests/ApprovalCapabilityTests.cs
git commit -m "feat(approval-mode): admin.approvalPolicy cap + narrow set; remove admin.firm ghost

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Wire swap — provisioning DTOs carry `ApprovalMode`, drop the bool

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs:4-14`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs:43-88`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs:71`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/AdminTests.cs` (CreateClientRequest / ClientRegistrationResponse usages)
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ClientRegistrationTenancyFieldsTests.cs:76`
- Test: add to `Backend/Accounting101.Ledger.Api.Tests/AdminTests.cs`

**Interfaces:**
- Consumes: `ApprovalMode` (Task 1), `ApprovalPolicy.ModeOf` (Task 1).
- Produces: `CreateClientRequest.ApprovalMode` (`ApprovalMode`, replaces the bool); `ClientRegistrationResponse(Guid, string, string, ApprovalMode, int)` (positional `ApprovalMode` replaces the bool). Create defaults `Unspecified → TwoPerson`.

- [ ] **Step 1: Write the failing tests**

Add to `Backend/Accounting101.Ledger.Api.Tests/AdminTests.cs` (new `[Fact]`s; keep the file's existing usings, add `using Accounting101.Ledger.Contracts;` and `using System.Text.Json;` if absent):

```csharp
    [Fact]
    public async Task Create_client_without_mode_defaults_to_two_person()
    {
        HttpResponseMessage resp = await fixture.AdminClient().PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "Defaults Co", DatabaseName = null, FiscalYearEndMonth = 12 });
        resp.EnsureSuccessStatusCode();
        ClientRegistrationResponse body = (await resp.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal(ApprovalMode.TwoPerson, body.ApprovalMode);
    }

    [Fact]
    public async Task Create_client_echoes_explicit_mode_as_a_string()
    {
        HttpResponseMessage resp = await fixture.AdminClient().PostAsJsonAsync(
            "/admin/clients", new CreateClientRequest { Name = "SelfCo", DatabaseName = null, ApprovalMode = ApprovalMode.SelfApprove, FiscalYearEndMonth = 12 });
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("SelfApprove", json);                 // string on the wire
        Assert.DoesNotContain("RequireSegregationOfDuties", json); // bool is gone
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: FAIL — `CreateClientRequest.ApprovalMode` / `ClientRegistrationResponse.ApprovalMode` do not exist (compile error).

- [ ] **Step 3: Swap the DTO fields**

In `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`, replace `CreateClientRequest` (lines 4-11) and `ClientRegistrationResponse` (lines 13-14):

```csharp
/// <summary>Provision a new client (a set of books). The ledger database name is generated if omitted.</summary>
public sealed record CreateClientRequest
{
    public required string Name { get; init; }
    public string? DatabaseName { get; init; }
    /// <summary>Approval posture. Defaults to TwoPerson when omitted (see the create handler).</summary>
    public ApprovalMode ApprovalMode { get; init; }
    /// <summary>Month (1-12) the fiscal year ends; defaults to December.</summary>
    public int FiscalYearEndMonth { get; init; } = 12;
}

public sealed record ClientRegistrationResponse(
    Guid Id, string Name, string DatabaseName, ApprovalMode ApprovalMode, int FiscalYearEndMonth);
```

(No new using needed — `ApprovalMode` is in this same `Accounting101.Ledger.Contracts` namespace.)

- [ ] **Step 4: Update the create/get/list handlers**

In `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`:

**CreateClient** — replace the registration build + response (lines 43-56):

```csharp
        ApprovalMode mode = request.ApprovalMode == ApprovalMode.Unspecified
            ? ApprovalMode.TwoPerson
            : request.ApprovalMode;

        ClientRegistration registration = new()
        {
            Id = id,
            Name = request.Name,
            DatabaseName = database,
            ApprovalMode = mode,
            FiscalYearEndMonth = request.FiscalYearEndMonth,
        };
        await control.RegisterClientAsync(registration, cancellationToken);

        return Results.Created(
            $"/admin/clients/{id}",
            new ClientRegistrationResponse(id, registration.Name, registration.DatabaseName,
                ApprovalPolicy.ModeOf(registration), FiscalYear.MonthOf(registration)));
```

**SetFiscalYearEnd** — in its response (line 78-79) replace `registration.RequireSegregationOfDuties` with `ApprovalPolicy.ModeOf(registration)`:

```csharp
        return Results.Ok(new ClientRegistrationResponse(registration.Id, registration.Name, registration.DatabaseName,
            ApprovalPolicy.ModeOf(registration), FiscalYear.MonthOf(registration)));
```

**ListClients** — in the projection (line 86) replace `c.RequireSegregationOfDuties` with `ApprovalPolicy.ModeOf(c)`:

```csharp
            .Select(c => new ClientRegistrationResponse(c.Id, c.Name, c.DatabaseName, ApprovalPolicy.ModeOf(c), FiscalYear.MonthOf(c)))
```

Add `using Accounting101.Ledger.Contracts;` at the top of `AdminEndpoints.cs` if `ApprovalMode` is not already resolvable (it already imports `Accounting101.Ledger.Contracts` at line 3 — no change needed).

- [ ] **Step 5: Fix the broken call sites**

`AdminCapabilityTests.cs:71` — replace `RequireSegregationOfDuties = false` in the `CreateClientRequest` initializer:

```csharp
            "/admin/clients", new CreateClientRequest { Name = "New Co", DatabaseName = null, ApprovalMode = ApprovalMode.SelfApprove, FiscalYearEndMonth = 12 });
```

`AdminTests.cs` — for each `CreateClientRequest` initializer that sets `RequireSegregationOfDuties`, delete that assignment (rely on the default) or set `ApprovalMode = ApprovalMode.TwoPerson`. For any assertion reading `.RequireSegregationOfDuties` off a `ClientRegistrationResponse`, change it to `.ApprovalMode` and compare against the expected `ApprovalMode` value.

`ClientRegistrationTenancyFieldsTests.cs:76` — this raw-BSON legacy document test writes `{ "RequireSegregationOfDuties", false }`. Leave that key (it validates legacy deserialization). If the test then asserts anything about the resolved mode, assert `ApprovalPolicy.ModeOf(...) == ApprovalMode.SelfApprove` for that legacy doc; otherwise leave it untouched.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — the two new create tests green; all previously-passing admin tests green after the call-site fixes. Grep `RequireSegregationOfDuties` under `Backend/**/*.cs` — the only remaining references should be `ClientRegistration.cs` (the legacy field), `ApprovalPolicy.cs` (the normalizer), `ApiFixture.cs` (the `requireSod` seeder param), and the legacy BSON test.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/AdminTests.cs \
        Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs \
        Backend/Accounting101.Ledger.Api.Tests/ClientRegistrationTenancyFieldsTests.cs
git commit -m "feat(approval-mode): swap provisioning DTOs to ApprovalMode (drop the bool on the wire)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Read/set API — `GET`/`PUT /clients/{id}/approval-policy`

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/ApprovalPolicyEndpoints.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (add two DTOs)
- Modify: `Accounting101.Host/Program.cs:102` (map the endpoints)
- Test: `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyEndpointTests.cs`

**Interfaces:**
- Consumes: `ApprovalMode`, `ApprovalPolicy.ModeOf`, `Capabilities.AdminApprovalPolicy`, `AdminAuthorization.MayAsync(user, clientId, capability, actorFactory, control, ct)`, `ControlStore.GetClientAsync`/`RegisterClientAsync`.
- Produces: `record ApprovalPolicyResponse(ApprovalMode Mode)`; `record SetApprovalPolicyRequest(ApprovalMode Mode)`; routes `GET`/`PUT /clients/{clientId:guid}/approval-policy`; `MapApprovalPolicyEndpoints()` extension.

- [ ] **Step 1: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalPolicyEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http)> MemberWithAsync(params string[] caps)
    {
        SeededClient c = await fixture.SeedClientAsync("PolicyCaps");
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], caps);
        return (c.ClientId, fixture.ClientFor(userId, "Member"));
    }

    [Fact]
    public async Task Holder_may_set_then_get_reflects_it()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminApprovalPolicy, Capabilities.GlRead);

        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.AutoApprove));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        ApprovalPolicyResponse got = (await (await http.GetAsync(
            $"/clients/{clientId}/approval-policy")).Content.ReadFromJsonAsync<ApprovalPolicyResponse>())!;
        Assert.Equal(ApprovalMode.AutoApprove, got.Mode);
    }

    [Fact]
    public async Task Member_without_cap_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/approval-policy");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_set()
    {
        SeededClient c = await fixture.SeedClientAsync("PolicyDeploy");
        HttpResponseMessage resp = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.SelfApprove));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Setting_unspecified_is_rejected()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminApprovalPolicy);
        HttpResponseMessage resp = await http.PutAsJsonAsync(
            $"/clients/{clientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.Unspecified));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Get_resolves_legacy_client_to_two_person()
    {
        SeededClient c = await fixture.SeedClientAsync("LegacySod", requireSod: true); // no ApprovalMode stored
        HttpResponseMessage resp = await fixture.AdminClient().GetAsync($"/clients/{c.ClientId}/approval-policy");
        ApprovalPolicyResponse got = (await resp.Content.ReadFromJsonAsync<ApprovalPolicyResponse>())!;
        Assert.Equal(ApprovalMode.TwoPerson, got.Mode);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: FAIL — `SetApprovalPolicyRequest` / `ApprovalPolicyResponse` and the route do not exist.

- [ ] **Step 3: Add the DTOs**

Append to `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`:

```csharp
/// <summary>A client's current approval mode.</summary>
public sealed record ApprovalPolicyResponse(ApprovalMode Mode);

/// <summary>Change a client's approval mode. <see cref="ApprovalMode.Unspecified"/> is rejected (422).</summary>
public sealed record SetApprovalPolicyRequest(ApprovalMode Mode);
```

- [ ] **Step 4: Create the endpoints**

Create `Backend/Accounting101.Ledger.Api/Endpoints/ApprovalPolicyEndpoints.cs`:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Per-client approval-mode policy: read and change a client's <see cref="ApprovalMode"/>. Gated by
/// <c>admin.approvalPolicy</c> (a deployment admin overrides). Weakening segregation of duties is a
/// sensitive single lever, so it rides its own capability rather than general client admin.
/// </summary>
public static class ApprovalPolicyEndpoints
{
    public static void MapApprovalPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/clients/{clientId:guid}/approval-policy").RequireAuthorization();
        g.MapGet("", GetApprovalPolicy);
        g.MapPut("", SetApprovalPolicy);
    }

    private static async Task<IResult> GetApprovalPolicy(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();
        return Results.Ok(new ApprovalPolicyResponse(ApprovalPolicy.ModeOf(client)));
    }

    private static async Task<IResult> SetApprovalPolicy(
        Guid clientId, SetApprovalPolicyRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        if (request.Mode == ApprovalMode.Unspecified)
            return Results.Problem("Mode must be TwoPerson, SelfApprove, or AutoApprove.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        client.ApprovalMode = request.Mode;
        await control.RegisterClientAsync(client, ct);
        return Results.Ok(new ApprovalPolicyResponse(request.Mode));
    }
}
```

- [ ] **Step 5: Map the endpoints**

In `Accounting101.Host/Program.cs`, after `app.MapMemberEndpoints();` (line 102), add:

```csharp
app.MapApprovalPolicyEndpoints();
```

(Add `using Accounting101.Ledger.Api.Endpoints;` at the top of `Program.cs` only if it is not already imported — the other `Map*Endpoints` calls mean it already is.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — all five `ApprovalPolicyEndpointTests` green.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/ApprovalPolicyEndpoints.cs \
        Backend/Accounting101.Ledger.Contracts/AdminContracts.cs \
        Accounting101.Host/Program.cs \
        Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyEndpointTests.cs
git commit -m "feat(approval-mode): GET/PUT /clients/{id}/approval-policy gated on admin.approvalPolicy

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Enforcement — SoD guard reroute + AutoApprove for post (single & batch)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (add helpers; edit `PostEntry`, `PostBatch`, `ApproveEntry`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/ApprovalModeEnforcementTests.cs`

**Interfaces:**
- Consumes: `ApprovalMode`, `ApprovalPolicy.ModeOf`, `ControlStore.GetClientAsync`, `LedgerContext` (`.Ledger.Service.ApproveAsync`, `.Actor`), `PostingState.PendingApproval`, `JournalEntry` (`.Id`, `.Posting`).
- Produces: `private static Task<bool> AutoApproveAsync(Guid, ControlStore, CancellationToken)`; `private static Task<JournalEntry> FinalizeAsync(bool, JournalEntry, LedgerContext, CancellationToken)`. `PostEntry`/`PostBatch` gain a `ControlStore control` parameter. `ApproveEntry`'s guard becomes `ApprovalPolicy.ModeOf(client) == ApprovalMode.TwoPerson`.

- [ ] **Step 1: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/ApprovalModeEnforcementTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalModeEnforcementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static PostEntryRequest Balanced(Guid? id, string date, Guid debit, Guid credit, decimal amount = 100m) =>
        new(id, DateOnly.Parse(date), null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    private static async Task<PostEntryResponse> PostAsync(HttpClient http, Guid clientId, PostEntryRequest req)
    {
        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/entries", req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PostEntryResponse>())!;
    }

    [Fact]
    public async Task Auto_approve_lands_a_single_post_as_posted()
    {
        SeededClient c = await fixture.SeedClientAsync("AutoSingle", approvalMode: ApprovalMode.AutoApprove);
        PostEntryResponse body = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("Posted", body.Posting);
    }

    [Fact]
    public async Task Auto_approve_lands_a_batch_as_posted()
    {
        SeededClient c = await fixture.SeedClientAsync("AutoBatch", approvalMode: ApprovalMode.AutoApprove);
        HttpResponseMessage resp = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries/batch",
            new PostBatchRequest([
                Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()),
                Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()),
            ]));
        resp.EnsureSuccessStatusCode();
        List<PostEntryResponse> body = (await resp.Content.ReadFromJsonAsync<List<PostEntryResponse>>())!;
        Assert.All(body, e => Assert.Equal("Posted", e.Posting));
    }

    [Fact]
    public async Task Self_approve_leaves_a_post_pending_until_approved()
    {
        SeededClient c = await fixture.SeedClientAsync("SelfMode", approvalMode: ApprovalMode.SelfApprove);
        PostEntryResponse body = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("PendingApproval", body.Posting);
    }

    [Fact]
    public async Task Self_approve_lets_the_author_approve_their_own_entry()
    {
        SeededClient c = await fixture.SeedClientAsync("SelfApprove", approvalMode: ApprovalMode.SelfApprove);
        PostEntryResponse posted = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        HttpResponseMessage approve = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    [Fact]
    public async Task Two_person_forbids_the_author_approving_their_own_entry()
    {
        SeededClient c = await fixture.SeedClientAsync("TwoPerson", approvalMode: ApprovalMode.TwoPerson);
        PostEntryResponse posted = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        HttpResponseMessage approve = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }

    [Fact]
    public async Task Two_person_allows_a_different_approver()
    {
        SeededClient c = await fixture.SeedClientAsync("TwoPersonOk", approvalMode: ApprovalMode.TwoPerson);
        PostEntryResponse posted = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        HttpClient approver = await fixture.AddMemberAsync(c.ClientId, Accounting101.Ledger.Api.Control.LedgerRole.Approver);
        HttpResponseMessage approve = await approver.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: FAIL — under AutoApprove the posts still return `"PendingApproval"` (the helpers/hooks don't exist yet). The TwoPerson/SelfApprove cases should already pass (behavior-preserving via the normalizer), which is fine.

- [ ] **Step 3: Add the two helpers**

In `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs`, add these two private methods (place them just above `ApproveEntry`, ~line 273):

```csharp
    /// <summary>True iff the client's resolved approval mode is AutoApprove (host policy, control DB).</summary>
    private static async Task<bool> AutoApproveAsync(Guid clientId, ControlStore control, CancellationToken ct)
    {
        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        return client is not null && ApprovalPolicy.ModeOf(client) == ApprovalMode.AutoApprove;
    }

    /// <summary>Under AutoApprove, approve a still-pending entry inline with the current actor (writing the
    /// Approved audit event) and return the approved entry; otherwise return it unchanged. Idempotent — a
    /// non-pending entry is returned untouched — so it is safe on both the fresh-post and replay paths.</summary>
    private static async Task<JournalEntry> FinalizeAsync(
        bool autoApprove, JournalEntry entry, LedgerContext ctx, CancellationToken ct) =>
        autoApprove && entry.Posting == PostingState.PendingApproval
            ? await ctx.Ledger.Service.ApproveAsync(entry.Id, ctx.Actor!, ct)
            : entry;
```

- [ ] **Step 4: Reroute the SoD guard in `ApproveEntry`**

In `ApproveEntry` (lines 283-289), replace the guard:

```csharp
        // Approval policy (host policy, per client). TwoPerson requires the approver to differ from the
        // author; this also covers revisions and reversals, approved through this same endpoint.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is not null && ApprovalPolicy.ModeOf(client) == ApprovalMode.TwoPerson
            && entry.Audit.CreatedBy == ctx.Actor.UserId)
            return Results.Problem(
                "Segregation of duties: an entry must be approved by someone other than the person who entered it.",
                statusCode: StatusCodes.Status403Forbidden);
```

- [ ] **Step 5: Wire AutoApprove into `PostEntry`**

Add `ControlStore control` to the `PostEntry` signature (line 65-67):

```csharp
    private static async Task<IResult> PostEntry(
        Guid clientId, PostEntryRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
```

Immediately after the `ctx.Failed` guard (after line 70), compute the flag once:

```csharp
        bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);
```

Early-replay return (lines 82-83) — finalize the existing entry:

```csharp
            if (EntryComparison.SameFinancialContent(earlyExisting, earlyMapped!))
            {
                JournalEntry finalizedEarly = await FinalizeAsync(autoApprove, earlyExisting, ctx, cancellationToken);
                return Results.Ok(new PostEntryResponse(finalizedEarly.Id, finalizedEarly.Status.ToString(), finalizedEarly.Posting.ToString()));
            }
```

Fresh-post return (lines 120-122) — finalize the just-posted entry:

```csharp
        JournalEntry finalizedEntry = await FinalizeAsync(autoApprove, entry!, ctx, cancellationToken);
        return Results.Created(
            $"/clients/{clientId}/entries/{finalizedEntry.Id}",
            new PostEntryResponse(finalizedEntry.Id, finalizedEntry.Status.ToString(), finalizedEntry.Posting.ToString()));
```

Duplicate-key replay return (lines 108-109) — finalize the existing entry:

```csharp
                if (EntryComparison.SameFinancialContent(existing, entry!))
                {
                    JournalEntry finalizedDup = await FinalizeAsync(autoApprove, existing, ctx, cancellationToken);
                    return Results.Ok(new PostEntryResponse(finalizedDup.Id, finalizedDup.Status.ToString(), finalizedDup.Posting.ToString()));
                }
```

- [ ] **Step 6: Wire AutoApprove into `PostBatch`**

Add `ControlStore control` to the `PostBatch` signature (line 191-193):

```csharp
    private static async Task<IResult> PostBatch(
        Guid clientId, PostBatchRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
```

Immediately after the `ctx.Failed` guard (after line 196), compute the flag once:

```csharp
        bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);
```

Replace the fresh-post success block (lines 257-261) so each written entry is finalized:

```csharp
            IReadOnlyList<JournalEntry> written = await ctx.Ledger!.Service.PostBatchAsync(mappedEntries, ctx.Actor!, cancellationToken);
            List<PostEntryResponse> body = [];
            foreach (JournalEntry e in written)
            {
                JournalEntry finalized = await FinalizeAsync(autoApprove, e, ctx, cancellationToken);
                body.Add(new PostEntryResponse(finalized.Id, finalized.Status.ToString(), finalized.Posting.ToString()));
            }
            return Results.Created($"/clients/{clientId}/entries/batch", body);
```

**Note (accepted limitation):** the batch idempotent-**replay** path is left as-is (it does not auto-heal a still-pending straggler). This is a rare double-edge (crash mid-approve, then re-post-as-batch); the single-post replay path does heal, and the fresh batch path is what AutoApprove clients hit. Do not add replay-heal to the batch — it would risk approving entries in a partial-replay 422.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — all `ApprovalModeEnforcementTests` green, including the AutoApprove single + batch cases. The whole solution stays green (the reroute is behavior-preserving for legacy/TwoPerson clients).

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/ApprovalModeEnforcementTests.cs
git commit -m "feat(approval-mode): reroute SoD guard to ModeOf; AutoApprove single & batch posts

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Enforcement — AutoApprove for revise & reverse

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`ReviseEntry`, `ReverseEntry`)
- Test: add to `Backend/Accounting101.Ledger.Api.Tests/ApprovalModeEnforcementTests.cs`

**Interfaces:**
- Consumes: `AutoApproveAsync`, `FinalizeAsync` (Task 5); `ToEntryResponse` (existing); `ctx.Ledger.Service.ReviseAsync`/`ReverseAsync`.
- Produces: `ReviseEntry`/`ReverseEntry` gain a `ControlStore control` parameter and finalize their created entry.

- [ ] **Step 1: Write the failing tests**

Add to `Backend/Accounting101.Ledger.Api.Tests/ApprovalModeEnforcementTests.cs`. (`EntryResponse` is the shape `ToEntryResponse` returns; it exposes a `Posting` string like `PostEntryResponse`. If the concrete response type name differs, deserialize into a small local record with a `string Posting` property.)

```csharp
    [Fact]
    public async Task Auto_approve_lands_a_reversal_as_posted()
    {
        SeededClient c = await fixture.SeedClientAsync("AutoReverse", approvalMode: ApprovalMode.AutoApprove);
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        PostEntryResponse original = await PostAsync(c.Http, c.ClientId, Balanced(Guid.NewGuid(), "2026-03-31", debit, credit));
        Assert.Equal("Posted", original.Posting); // auto-approved, so it is reversible

        HttpResponseMessage resp = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{original.Id}/reverse",
            new { ReversalDate = "2026-03-31", Reason = "correct", SourceRef = (string?)null, SourceType = (string?)null });
        resp.EnsureSuccessStatusCode();
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Posted", doc.RootElement.GetProperty("posting").GetString());
    }
```

(Reverse requires the original to be `Posted`; under AutoApprove it already is — which is exactly why AutoApprove must cover the whole chain. If `reverse`'s request record differs from the anonymous object above, construct the real `ReverseRequest`/`ReverseEntryRequest` type instead.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: FAIL — the reversal comes back `"PendingApproval"` (revise/reverse don't finalize yet).

- [ ] **Step 3: Finalize in `ReviseEntry`**

Add `ControlStore control` to the `ReviseEntry` signature (line 327-329):

```csharp
    private static async Task<IResult> ReviseEntry(
        Guid clientId, Guid originalId, ReviseRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
```

Replace the success return (lines 356-357):

```csharp
            JournalEntry result = await ctx.Ledger.Service.ReviseAsync(originalId, replacement, ctx.Actor, request.Reason, cancellationToken);
            bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);
            JournalEntry finalized = await FinalizeAsync(autoApprove, result, ctx, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{finalized.Id}", ToEntryResponse(finalized));
```

- [ ] **Step 4: Finalize in `ReverseEntry`**

Add `ControlStore control` to the `ReverseEntry` signature (line 369-371):

```csharp
    private static async Task<IResult> ReverseEntry(
        Guid clientId, Guid originalId, ReverseRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
```

Replace the success return (lines 385-388):

```csharp
            JournalEntry reversal = await ctx.Ledger.Service.ReverseAsync(
                originalId, request.ReversalDate, ctx.Actor, request.Reason,
                request.SourceRef, request.SourceType, cancellationToken);
            bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);
            JournalEntry finalized = await FinalizeAsync(autoApprove, reversal, ctx, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{finalized.Id}", ToEntryResponse(finalized));
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — the reversal lands `"Posted"`; whole solution green.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/ApprovalModeEnforcementTests.cs
git commit -m "feat(approval-mode): AutoApprove revisions and reversals

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Frontend — `ApprovalPolicyService` + types

**Files:**
- Create: `UI/Angular/src/app/core/approval-policy/approval-policy.ts`
- Create: `UI/Angular/src/app/core/approval-policy/approval-policy.service.ts`
- Test: `UI/Angular/src/app/core/approval-policy/approval-policy.service.spec.ts`

**Interfaces:**
- Consumes: `environment.apiBaseUrl`, `ClientContextService.clientId()`.
- Produces: `type ApprovalMode = 'TwoPerson' | 'SelfApprove' | 'AutoApprove'`; `interface ApprovalPolicy { mode: ApprovalMode }`; `ApprovalPolicyService.get(): Observable<ApprovalPolicy>`, `.set(mode: ApprovalMode): Observable<ApprovalPolicy>` hitting `/clients/{id}/approval-policy`.

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/core/approval-policy/approval-policy.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApprovalPolicyService } from './approval-policy.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';

describe('ApprovalPolicyService', () => {
  let http: HttpTestingController;
  let service: ApprovalPolicyService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('c1');
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ApprovalPolicyService);
  });
  afterEach(() => http.verify());

  it('GETs the current policy', () => {
    let got: string | undefined;
    service.get().subscribe((p) => (got = p.mode));
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'AutoApprove' });
    expect(got).toBe('AutoApprove');
  });

  it('PUTs the chosen mode', () => {
    service.set('SelfApprove').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ mode: 'SelfApprove' });
    req.flush({ mode: 'SelfApprove' });
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `ng test --include='**/approval-policy.service.spec.ts' --watch=false`
Expected: FAIL — the service module does not exist.

- [ ] **Step 3: Create the types**

Create `UI/Angular/src/app/core/approval-policy/approval-policy.ts`:

```ts
export type ApprovalMode = 'TwoPerson' | 'SelfApprove' | 'AutoApprove';

export interface ApprovalPolicy {
  mode: ApprovalMode;
}
```

- [ ] **Step 4: Create the service**

Create `UI/Angular/src/app/core/approval-policy/approval-policy.service.ts`:

```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { ApprovalMode, ApprovalPolicy } from './approval-policy';

@Injectable({ providedIn: 'root' })
export class ApprovalPolicyService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(): string {
    return `${environment.apiBaseUrl}/clients/${this.client.clientId()}/approval-policy`;
  }

  get(): Observable<ApprovalPolicy> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<ApprovalPolicy>(this.base());
  }

  set(mode: ApprovalMode): Observable<ApprovalPolicy> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<ApprovalPolicy>(this.base(), { mode });
  }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `ng test --include='**/approval-policy.service.spec.ts' --watch=false`
Expected: PASS — both cases green.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/approval-policy/
git commit -m "feat(approval-mode): ApprovalPolicyService (GET/PUT approval-policy)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Frontend — approval-policy screen, route, nav

**Files:**
- Create: `UI/Angular/src/app/features/admin/approval-policy.ts`
- Test: `UI/Angular/src/app/features/admin/approval-policy.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (import + route, near lines 53-56 / 178-182)
- Modify: `UI/Angular/src/app/layout/nav.ts:36-43` (Administration section)

**Interfaces:**
- Consumes: `ApprovalPolicyService` (Task 7), `ApprovalMode` (Task 7), `CanDirective` (`*appCan`), `HlmButton`, `canWrite` guard (route `data.requiredCapability` + `data.fallback`).
- Produces: `ApprovalPolicyScreen` component at route `admin/approval-policy` (guarded by `admin.approvalPolicy`); nav entry under Administration.

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/features/admin/approval-policy.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApprovalPolicyScreen } from './approval-policy';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function seed() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.approvalPolicy')],
  });
  TestBed.inject(ClientContextService).select('c1');
}

describe('ApprovalPolicyScreen', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('loads the current mode and PUTs the chosen one', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson' });
    f.detectChanges();

    const c = f.componentInstance as ApprovalPolicyScreen;
    expect(c.selected()).toBe('TwoPerson');
    expect(c.options.length).toBe(3);

    c.select('AutoApprove');
    c.save();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ mode: 'AutoApprove' });
    req.flush({ mode: 'AutoApprove' });
    expect(c.saved()).toBe(true);
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `ng test --include='**/approval-policy.spec.ts' --watch=false`
Expected: FAIL — the component does not exist.

- [ ] **Step 3: Create the screen**

Create `UI/Angular/src/app/features/admin/approval-policy.ts`:

```ts
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { ApprovalPolicyService } from '../../core/approval-policy/approval-policy.service';
import { ApprovalMode } from '../../core/approval-policy/approval-policy';

interface ModeOption { value: ApprovalMode; label: string; description: string; lowControl?: boolean; }

@Component({
  selector: 'app-approval-policy',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective],
  template: `
    <h1 class="text-xl font-semibold mb-4">Approval policy</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    @if (saved()) { <p class="text-green-600 mb-2">Saved.</p> }
    <p class="text-sm text-muted-foreground mb-3">Controls how journal entries and subledger postings
       reach the books for this client.</p>

    <div class="space-y-3">
      @for (o of options; track o.value) {
        <label class="flex items-start gap-3">
          <input type="radio" name="mode" [value]="o.value" [checked]="selected() === o.value"
                 (change)="select(o.value)" class="mt-1" />
          <span>
            <span class="font-medium">{{ o.label }}</span>
            @if (o.lowControl) {
              <span class="ms-2 text-xs rounded bg-amber-100 text-amber-800 px-1.5 py-0.5">removes a review step</span>
            }
            <span class="block text-sm text-muted-foreground">{{ o.description }}</span>
          </span>
        </label>
      }
    </div>

    <div class="flex gap-2 mt-4">
      <button *appCan="'admin.approvalPolicy'" hlmBtn [disabled]="selected() === null" (click)="save()">Save</button>
    </div>
  `,
})
export class ApprovalPolicyScreen {
  private readonly service = inject(ApprovalPolicyService);

  readonly options: ModeOption[] = [
    { value: 'TwoPerson', label: 'Two-person approval',
      description: 'An entry must be approved by someone other than its author (segregation of duties).' },
    { value: 'SelfApprove', label: 'Self-approve',
      description: 'The author may approve their own entries.' },
    { value: 'AutoApprove', label: 'Auto-approve',
      description: 'Entries reach the books at post time. No second review; still fully audited.', lowControl: true },
  ];

  readonly selected = signal<ApprovalMode | null>(null);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);

  constructor() {
    this.service.get().subscribe({
      next: (p) => this.selected.set(p.mode),
      error: () => this.error.set('Could not load the approval policy.'),
    });
  }

  select(mode: ApprovalMode): void {
    this.selected.set(mode);
    this.saved.set(false);
  }

  save(): void {
    const mode = this.selected();
    if (!mode) return;
    this.error.set(null);
    this.service.set(mode).subscribe({
      next: () => this.saved.set(true),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }
}
```

- [ ] **Step 4: Register the route**

In `UI/Angular/src/app/app.routes.ts`, add the import alongside the other admin imports (near lines 53-56):

```ts
import { ApprovalPolicyScreen } from './features/admin/approval-policy';
```

Add the route alongside the other admin routes (near lines 178-182):

```ts
  { path: 'admin/approval-policy', component: ApprovalPolicyScreen, canActivate: [canWrite], data: { requiredCapability: 'admin.approvalPolicy', fallback: '/admin/users' } },
```

- [ ] **Step 5: Add the nav entry**

In `UI/Angular/src/app/layout/nav.ts`, add one item to the Administration section (after the "Client" line, ~line 40):

```ts
    { label: 'Approval policy', path: '/admin/approval-policy', area: 'admin' },
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `ng test --include='**/approval-policy.spec.ts' --watch=false`
Expected: PASS. Also run the nav spec to confirm the added link didn't break it:
Run: `ng test --include='**/nav.spec.ts' --watch=false`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/admin/approval-policy.ts \
        UI/Angular/src/app/features/admin/approval-policy.spec.ts \
        UI/Angular/src/app/app.routes.ts \
        UI/Angular/src/app/layout/nav.ts
git commit -m "feat(approval-mode): approval-policy admin screen, route, and nav entry

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Dev-stack smoke (verification gate — no code)

**Files:** none (manual verification against the running dev stack).

This is the layer that catches real wire-serialization bugs (the recurring enum-as-number trap). Run it before declaring the branch done. Requires the local dev stack (or the JordanSoft container stack) up, with a client id and an auth token that carries `admin.approvalPolicy` (or the deployment `admin=true` dev token — `Authorization: DevToken <token>`, not `Bearer`).

- [ ] **Step 1: Confirm string-on-wire for the policy read**

```bash
curl -s -H "Authorization: DevToken <token>" \
  http://localhost:5000/clients/<clientId>/approval-policy
```
Expected: JSON `{"mode":"TwoPerson"}` — a **string**, never `{"mode":1}`.

- [ ] **Step 2: Flip to AutoApprove**

```bash
curl -s -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"mode":"AutoApprove"}' \
  http://localhost:5000/clients/<clientId>/approval-policy
```
Expected: `200` and body `{"mode":"AutoApprove"}`.

- [ ] **Step 3: Post a balanced journal entry and confirm it lands Posted**

```bash
curl -s -X POST -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"effectiveDate":"2026-07-11","lines":[{"accountId":"<a>","direction":"Debit","amount":10},{"accountId":"<b>","direction":"Credit","amount":10}]}' \
  http://localhost:5000/clients/<clientId>/entries
```
Expected: `201` and `"posting":"Posted"` (not `"PendingApproval"`) — auto-approved at post, no separate approval call.

- [ ] **Step 4: Confirm the audit trail shows both events**

Open the client's audit trail (UI `/audit/trail`, or the audit read endpoint) and confirm the entry has **both** a `Created` and an `Approved` event, both stamped with the posting actor.

- [ ] **Step 5: Restore the client's intended mode**

```bash
curl -s -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"mode":"TwoPerson"}' \
  http://localhost:5000/clients/<clientId>/approval-policy
```
(Set back to whatever the client should actually run — do not leave a real client on AutoApprove unintentionally.)

- [ ] **Step 6: Open the screen**

Navigate to `/admin/approval-policy` as a user holding `admin.approvalPolicy`. Confirm three radios render with descriptions, Auto-approve shows the "removes a review step" flag, the current mode is preselected, and Save persists (re-open the screen to confirm it reloads the saved value).

---

## Self-Review

**Spec coverage:**
- Model (enum, 1-based, `Unspecified` sentinel; storage field; `ApprovalPolicy.ModeOf` normalizer; lazy migration) → **Task 1**. ✅
- Migration mapping `true→TwoPerson`, `false→SelfApprove`; legacy bool kept read-only → **Task 1** (field comment) + **Task 5** guard reroute preserves legacy behavior. ✅
- Wire clean swap (`CreateClientRequest`/`ClientRegistrationResponse` carry `ApprovalMode`, drop bool; create defaults `Unspecified→TwoPerson`) → **Task 3**. ✅
- Enforcement across the four creation handlers + module transitivity + reuse `ApproveAsync` + self-heal on single-post replay + accepted batch-replay limitation → **Tasks 5 & 6** (module coverage is transitive; a `ViaModule` post routes through `PostEntry`/`PostBatch`, already finalized — no per-module code). ✅
- Authorization: new `admin.approvalPolicy` cap + seeded "Approval Policy Admin" set + Admin preset + `All` → **Task 2**; endpoint gating → **Task 4**. ✅
- Ghost-cap removal (`admin.firm`) → **Task 2**. ✅
- API: `GET`/`PUT /clients/{id}/approval-policy`, `Unspecified` → 422 → **Task 4**. ✅
- UI: dedicated `admin/approval-policy` radio-group screen, guarded, Administration nav, AutoApprove flagged, no cross-guarding → **Tasks 7 & 8**. ✅
- Testing: normalizer, string-on-wire serialization, TwoPerson/SelfApprove/AutoApprove across paths, authz, wire, UI, dev-stack smoke → **Tasks 1–9**. ✅

**Placeholder scan:** No `TBD`/`TODO`/"add error handling"/"similar to Task N". Every code step shows full code; every test step shows full test code. ✅

**Type consistency:** `ApprovalMode` (Contracts) used identically in enum/DTOs/service/screen; `ApprovalPolicy.ModeOf(ClientRegistration) → ApprovalMode` consistent across Tasks 1/3/4/5; `AutoApproveAsync`/`FinalizeAsync` signatures defined in Task 5 and reused verbatim in Task 6; `ApprovalPolicyResponse`/`SetApprovalPolicyRequest` consistent between Task 4 (backend) and Task 7 (`{ mode }` body); component `ApprovalPolicyScreen` (distinct from the `ApprovalPolicy` interface) consistent across Task 8 and its route/spec. ✅
