# Module-Owned Entry Guard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the ledger refuse a raw-user void/reverse/revise of a module-owned journal entry, so a subledger-owned entry can only be corrected through its owning module — closing the direct-void hole across all seven modules at once.

**Architecture:** One engine change (the ledger's mutation endpoints branch on the target entry's `Audit.ViaModule`: a module-owned entry requires the owning module's credential and module authorization, mirroring the existing post path in `LedgerGateway.ResolveForPostAsync`; a manual entry is unchanged). Plus a uniform 2-line change to every module's `HttpLedgerClient` so its own reverse/void calls carry the module credential — this lands **first**, as a safe no-op, so module voids keep working the instant the guard flips on.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB, xUnit, `WebApplicationFactory` host fixtures, EphemeralMongo.

## Global Constraints

- Module credential headers are exactly **`X-Module-Key`** and **`X-Module-Secret`** (copy from `HttpLedgerClient.PostAsync`, e.g. `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs:35-36`).
- The engine's module-auth path is **`LedgerGateway.ResolveForPostAsync`** (`Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs:46-68`) — the guard mirrors its `moduleAuth.AuthenticateAsync()` → `ModuleAccess.AuthorizeAsync(module, module.Key, actor.UserId, clientId, ModuleAccessLevel.Write, ct)` shape. Do not invent a new auth mechanism.
- A module-owned entry is one whose **`entry.Audit.ViaModule` is non-null** (the module key string, e.g. `"inventory"`). Manual entries have `ViaModule == null`.
- Refusal of a raw mutation of a module-owned entry is **HTTP 409** with the message: `Entry belongs to module '{viaModule}'; void or reverse it through that module, not the raw journal.`
- **Break-glass admin override is OUT OF SCOPE** for this plan (its step-up + audit shape is an open design question — spec §11). Module-owned entries are therefore **fully default-closed** to raw mutation for now; a documented follow-up will add the admin escape hatch. Note this in the code comment where the guard refuses.
- Do not weaken or delete existing tests to make them pass. A test that currently voids a *module-sourced* entry via the *raw* endpoint and expects success was relying on the hole — update it to either drive the module's own void or assert the new 409 (Task 6).
- Commit trailer on every commit: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`. Stage explicit paths only, never `-A`.

---

### Task 1: Module clients attach the credential on reverse/void (safe no-op)

Add the module-credential headers to `ReverseAsync` and `VoidAsync` in all seven module `HttpLedgerClient`s, mirroring their existing `PostAsync`. Before the engine guard exists these endpoints ignore the headers, so this is behavior-neutral and every suite stays green — it just makes the modules ready for the guard.

**Files (modify all seven):**
- `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs`
- `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs`
- `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`
- `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`
- `Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs`
- `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/HttpLedgerClient.cs`
- `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/HttpLedgerClient.cs`

**Interfaces:**
- Consumes: each client already injects `[FromKeyedServices("<key>")] ModuleCredential credential` and has `credential.Key` / `credential.Secret`.
- Produces: reverse/void requests that carry `X-Module-Key`/`X-Module-Secret`.

- [ ] **Step 1: In each client, in BOTH `ReverseAsync` and `VoidAsync`, add the two credential headers immediately after the `Forwarded(...)` message is built and before `message.Content = ...`.**

In every file, both methods, insert (this is the same pair of lines already present in that file's `PostAsync`):

```csharp
        message.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        message.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
```

(In `PostAsync` the local is named `request`; in `ReverseAsync`/`VoidAsync` it is named `message` — use `message`.)

- [ ] **Step 2: Update the XML-doc paragraph on each client that says reverse/void do NOT attach the credential.**

Replace the "`ReverseAsync` and `VoidAsync` do NOT attach the credential …" paragraph with:

```csharp
/// <para>
/// <see cref="ReverseAsync"/> and <see cref="VoidAsync"/> also attach the module credential
/// (<c>X-Module-Key</c>/<c>X-Module-Secret</c>). The engine's mutation endpoints require it to authorize a
/// correction of a module-owned entry: the correction must be driven by the owning module, not a raw user.
/// </para>
```

- [ ] **Step 3: Build the whole solution.**

Run: `dotnet build Accounting101.slnx`
Expected: build succeeds, no errors.

- [ ] **Step 4: Run the module test suites to confirm the no-op did not change behavior.**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests/Accounting101.Inventory.Tests.csproj`
Expected: PASS (75/75). The engine still ignores the credential on reverse/void, so nothing changed yet.

- [ ] **Step 5: Commit.**

```bash
git add Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs \
        Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs \
        Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs \
        Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs \
        Modules/Banking/Cash/Accounting101.Banking.Cash.Api/HttpLedgerClient.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/HttpLedgerClient.cs
git commit -m "refactor(ledger-seam): module clients send credential on reverse/void

Prepares every module's ledger client to drive corrections of its own entries
through the module-authorized path. No-op until the engine guard lands.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Gateway helpers + VoidEntry guard

Add the two gateway helpers (`ResolveMemberAsync`, `AuthorizeEntryMutationAsync`) and rewire `VoidEntry` to load the entry, branch on `Audit.ViaModule`, and refuse a raw void of a module-owned entry.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (the `VoidEntry` handler, currently at lines 198-216)
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleEntryGuardTests.cs` (new) for the manual-entry paths; `Modules/Inventory/Accounting101.Inventory.Tests/MovementVoidE2eTests.cs` (extend) for the module-owned paths.

**Interfaces:**
- Consumes: `LedgerGateway(IActorFactory actorFactory, ControlStore control, ClientLedgerFactory ledgers, ModuleAccess moduleAccess)`; `IModuleAuthenticator.AuthenticateAsync()` → `ModuleIdentity?` (has `.Key`); `ModuleAccess.AuthorizeAsync(ModuleIdentity, string targetNamespace, Guid userId, Guid clientId, ModuleAccessLevel, CancellationToken)` → `ModuleAccessDecision` (`.Allowed`); `Membership.Capabilities`; `Capabilities.CapabilityForPermission(Permission)`; `JournalEntry.Audit.ViaModule` (string?); `ctx.Ledger.Journal.GetAsync(entryId, ct)`.
- Produces: `LedgerGateway.ResolveMemberAsync(ClaimsPrincipal, Guid, CancellationToken) → Task<LedgerContext>` and `LedgerGateway.AuthorizeEntryMutationAsync(ClaimsPrincipal, Guid clientId, string? entryViaModule, Permission rawPermission, IModuleAuthenticator, CancellationToken) → Task<IResult?>` (null = allowed). Consumed verbatim by Tasks 3 and 4.

- [ ] **Step 1: Write the failing tests.**

Create `Backend/Accounting101.Ledger.Api.Tests/ModuleEntryGuardTests.cs`. Mirror the existing ledger host-fixture test style (see any test in `Backend/Accounting101.Ledger.Api.Tests/` for the fixture + auth-header helpers; use the same fixture type and a member who holds `gl.void`). Write three tests:

```csharp
// 1. A raw user can still void a MANUAL entry (ViaModule == null) — unchanged.
[Fact] public async Task Raw_void_of_a_manual_entry_still_succeeds() { /* post a normal entry (no module
    credential), approve if needed, void via POST /clients/{id}/entries/{entryId}/void as a gl.void user →
    expect 200 OK. */ }

// 2. A member WITHOUT gl.void voiding a manual entry is still refused.
[Fact] public async Task Raw_void_without_permission_is_forbidden() { /* void a manual entry as a member
    lacking gl.void → expect 403. */ }
```

For the module-owned path, extend `Modules/Inventory/Accounting101.Inventory.Tests/MovementVoidE2eTests.cs` (it already has the host fixture, chart setup, and posts real `ViaModule="inventory"` entries):

```csharp
// 3. A raw user CANNOT void a module-owned entry directly — must go through the module.
[Fact]
public async Task A_raw_journal_void_of_a_module_owned_entry_is_refused()
{
    (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (holds inventory.write + gl.void)
    await SetUpChartAsync(http, clientId);
    ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

    HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
        new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), null));
    StockMovementView mv = (await created.Content.ReadFromJsonAsync<StockMovementView>())!;
    EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
        $"/clients/{clientId}/entries?sourceRef={mv.Movement.Id}"))!;
    Guid moduleEntryId = entries.Single().Id;

    // Raw void through the journal endpoint (no module credential) is refused.
    HttpResponseMessage raw = await http.PostAsJsonAsync(
        $"/clients/{clientId}/entries/{moduleEntryId}/void", new { Reason = "should be refused" });
    Assert.Equal(HttpStatusCode.Conflict, raw.StatusCode);
    Assert.Contains("through that module", await raw.Content.ReadAsStringAsync());

    // The entry is untouched — still active/posting-pending.
    EntryResponse after = (await http.GetFromJsonAsync<EntryResponse[]>(
        $"/clients/{clientId}/entries?sourceRef={mv.Movement.Id}"))!.Single();
    Assert.Equal("Active", after.Status);
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleEntryGuard"` and `dotnet test Modules/Inventory/Accounting101.Inventory.Tests/Accounting101.Inventory.Tests.csproj --filter "FullyQualifiedName~A_raw_journal_void_of_a_module_owned_entry_is_refused"`
Expected: test 3 FAILS (raw void currently returns 200, not 409); tests 1-2 likely PASS already (raw path unchanged).

- [ ] **Step 3: Add the gateway helpers.**

In `Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs`, add these two methods to the `LedgerGateway` class:

```csharp
    /// <summary>Resolve actor + ledger for a caller who is a MEMBER of the client, without gating on any
    /// specific permission — so an entry-mutation endpoint can load the target entry before deciding the
    /// authorization path. Mutation authz is applied afterward via <see cref="AuthorizeEntryMutationAsync"/>.</summary>
    public async Task<LedgerContext> ResolveMemberAsync(
        ClaimsPrincipal user, Guid clientId, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null) return LedgerContext.Forbidden();
        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger);
    }

    /// <summary>Authorize a void/reverse/revise of an already-loaded entry. Two paths, decided by whether the
    /// target entry is module-owned:
    /// <list type="bullet">
    ///   <item><b>Manual entry</b> (<paramref name="entryViaModule"/> null): unchanged — the caller's role must
    ///     hold <paramref name="rawPermission"/>.</item>
    ///   <item><b>Module-owned entry</b>: the request MUST carry the owning module's credential
    ///     (<c>X-Module-Key</c>/<c>X-Module-Secret</c> whose key equals <paramref name="entryViaModule"/>) and that
    ///     module must be authorized (registered + enabled, caller a member). A raw caller — no matching module
    ///     credential — is refused (409): the correction must go through the owning module. (Break-glass admin
    ///     override is a documented follow-up; module-owned entries are default-closed to raw mutation.)</item>
    /// </list>
    /// Returns null when the mutation is allowed, or an <see cref="IResult"/> error (403/409) when refused.</summary>
    public async Task<IResult?> AuthorizeEntryMutationAsync(
        ClaimsPrincipal user, Guid clientId, string? entryViaModule, Permission rawPermission,
        IModuleAuthenticator moduleAuth, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);

        if (entryViaModule is null)
        {
            Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
            if (membership is null || !membership.Capabilities.Contains(Capabilities.CapabilityForPermission(rawPermission)))
                return Results.Forbid();
            return null;
        }

        ModuleIdentity? module = await moduleAuth.AuthenticateAsync();
        if (module is null || !string.Equals(module.Key, entryViaModule, StringComparison.Ordinal))
            return Results.Problem(
                $"Entry belongs to module '{entryViaModule}'; void or reverse it through that module, not the raw journal.",
                statusCode: StatusCodes.Status409Conflict);

        ModuleAccessDecision decision = await moduleAccess.AuthorizeAsync(
            module, module.Key, actor.UserId, clientId, ModuleAccessLevel.Write, cancellationToken);
        return decision == ModuleAccessDecision.Allowed ? null : Results.Forbid();
    }
```

(Add `using Microsoft.AspNetCore.Http;` if `Results`/`StatusCodes` are not already in scope in this file.)

- [ ] **Step 4: Rewire `VoidEntry` to use them.**

In `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs`, replace the `VoidEntry` handler (lines 198-216) with:

```csharp
    private static async Task<IResult> VoidEntry(
        Guid clientId, Guid entryId, VoidRequest? request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveMemberAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? entry = await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken);
        if (entry is null) return Results.NotFound();

        if (await gateway.AuthorizeEntryMutationAsync(
                user, clientId, entry.Audit.ViaModule, Permission.Void, moduleAuth, cancellationToken) is { } denied)
            return denied;

        try
        {
            JournalEntry voided = await ctx.Ledger.Service.VoidAsync(entryId, ctx.Actor, request?.Reason, cancellationToken);
            return Results.Ok(ToEntryResponse(voided));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }
```

- [ ] **Step 5: Run the Task-2 tests to verify they pass.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleEntryGuard"` and `dotnet test Modules/Inventory/Accounting101.Inventory.Tests/Accounting101.Inventory.Tests.csproj --filter "FullyQualifiedName~Void"`
Expected: all PASS — raw void of a module entry now 409; the module's own void (via `POST /movements/{id}/void`, which now sends the credential) still 200; manual-entry void unchanged.

- [ ] **Step 6: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/ModuleEntryGuardTests.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/MovementVoidE2eTests.cs
git commit -m "feat(ledger): guard void of module-owned entries — correct through the module

VoidEntry now branches on the target entry's Audit.ViaModule: a module-owned
entry requires the owning module's credential (mirrors the post path); a raw
void is refused 409. Manual entries unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: ReverseEntry guard

Apply the same branch to `ReverseEntry` (reuse the Task-2 gateway helpers).

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (the `ReverseEntry` handler, currently at lines 255+)
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleEntryGuardTests.cs` (extend); `Modules/Inventory/Accounting101.Inventory.Tests/MovementVoidE2eTests.cs` (extend).

**Interfaces:**
- Consumes: `LedgerGateway.ResolveMemberAsync`, `LedgerGateway.AuthorizeEntryMutationAsync` (Task 2), `Permission.Reverse`.

- [ ] **Step 1: Write the failing test.** Add to `ModuleEntryGuardTests.cs` a `Raw_reverse_of_a_module_owned_entry_is_refused` test (post a module entry via the inventory module as in Task 2, approve it so it is `Posting == "Posted"`, then `POST /entries/{id}/reverse` as a raw user with `gl.reverse` → expect 409 and body contains "through that module"). Keep a `Raw_reverse_of_a_manual_posted_entry_still_succeeds` test for the unchanged path.

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~Reverse"` — Expected: the module-owned reverse test FAILS (currently 200/201).

- [ ] **Step 3: Rewire `ReverseEntry`.** Replace its context resolution + not-found preamble with the member-resolve + load + `AuthorizeEntryMutationAsync(..., original.Audit.ViaModule, Permission.Reverse, moduleAuth, ...)` pattern from `VoidEntry` (Task 2 Step 4), keeping the rest of the handler (the `ReverseAsync(originalId, request.ReversalDate, ctx.Actor, request.Reason, ...)` call and its existing catch blocks) intact. Add `IModuleAuthenticator moduleAuth` to the handler's parameter list.

- [ ] **Step 4: Run to verify it passes.** Run the same filter — Expected: PASS. The inventory module's own void, when the spawned entry was approved (its reverse branch, already covered by `MovementVoidE2eTests`), still 200 because the module now sends the credential.

- [ ] **Step 5: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/ModuleEntryGuardTests.cs
git commit -m "feat(ledger): guard reverse of module-owned entries

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: ReviseEntry guard

Apply the same branch to `ReviseEntry` (currently at lines 218-253). Modules do not call revise, so there is no module-client change — this closes the raw-revise vector on module-owned entries.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (the `ReviseEntry` handler)
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleEntryGuardTests.cs` (extend)

**Interfaces:**
- Consumes: `LedgerGateway.ResolveMemberAsync`, `LedgerGateway.AuthorizeEntryMutationAsync` (Task 2), `Permission.Revise`.

- [ ] **Step 1: Write the failing test.** Add `Raw_revise_of_a_module_owned_entry_is_refused` (post a module entry via the inventory module, then `POST /entries/{id}/revise` as a raw user with `gl.revise` → expect 409). Keep a `Raw_revise_of_a_manual_entry_still_succeeds` for the unchanged path (revise a manual entry → 201 Created).

- [ ] **Step 2: Run to verify it fails.** Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~Revise"` — Expected: the module-owned revise test FAILS.

- [ ] **Step 3: Rewire `ReviseEntry`.** Add `IModuleAuthenticator moduleAuth` to the parameter list. Replace its `gateway.ResolveAsync(user, clientId, Permission.Revise, ...)` + not-found preamble with: `ResolveMemberAsync` → `GetAsync(originalId)` → `if (entry is null) return NotFound()` → `AuthorizeEntryMutationAsync(user, clientId, entry.Audit.ViaModule, Permission.Revise, moduleAuth, ct)` guard. Keep everything after (the `MapReplacement`, `ChartViolationsAsync`, `ReviseAsync`, and existing catch blocks) unchanged, referencing `ctx.Actor`/`ctx.Ledger` from the member-resolve.

- [ ] **Step 4: Run to verify it passes.** Run the same filter — Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/ModuleEntryGuardTests.cs
git commit -m "feat(ledger): guard revise of module-owned entries

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Full-suite reconciliation

The guard is a deliberate behavior change: any existing test that mutated a *module-sourced* entry via the *raw* endpoint and expected success was exercising the hole and must be reconciled.

**Files:**
- Modify: whichever existing test files the full-suite run flags (expected candidates: settlement E2E tests in `Modules/Receivables/.../Settlement/` and `Modules/Payables/.../Settlement/` that void/reverse via raw endpoints; the ledger engine's own `ReverseTests`/`ReopenTests` if they reverse module-stamped entries).

- [ ] **Step 1: Run the whole solution's tests.**

Run: `dotnet test Accounting101.slnx`
Expected: mostly green; note any failures.

- [ ] **Step 2: Triage each failure.** For each failing test, determine which case it is:
  - It voided/reversed a **manual** entry → should still pass; if it fails, the guard has a regression — fix the guard, not the test.
  - It voided/reversed a **module-sourced** entry via the **raw** endpoint expecting success → it was relying on the hole. Update it to drive the owning module's void/reverse surface instead (the correct path), or, if the test's intent was specifically to prove raw journal mutation, re-point it at a manual entry. Do NOT simply delete assertions.

- [ ] **Step 3: Re-run the whole suite.**

Run: `dotnet test Accounting101.slnx`
Expected: all PASS.

- [ ] **Step 4: Add the break-glass follow-up note.** In `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md`, under §11, append a line: `- Break-glass admin override for module-owned entries: NOT built in the guard plan (2026-07-09-module-entry-guard.md) — module-owned entries are default-closed to raw mutation. Follow-up must define the deployment-admin + step-up + audit shape before adding an override path.`

- [ ] **Step 5: Commit.**

```bash
git add <each reconciled test file> docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md
git commit -m "test(ledger): reconcile suites with the module-owned entry guard

Update tests that mutated module-sourced entries via the raw journal to drive
the owning module's correction surface instead. Note break-glass as follow-up.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the executor

- The guard changes the authorization *semantics* for a module-owned correction: it is now authorized as the **module** (caller needs the module's write capability + membership), no longer as the user's raw `gl.void`/`gl.reverse`. This is intentional and matches the post path — a clerk no longer needs raw GL-reversal rights to undo a module document. Existing tests whose users hold both still pass.
- The member-then-mutation-authz ordering means a member who lacks the raw permission can learn an entry exists (404 vs authz error) before being refused — acceptable, since the caller is already a verified member.
- Do not attempt the break-glass override in this plan; it is deliberately deferred (Global Constraints).
