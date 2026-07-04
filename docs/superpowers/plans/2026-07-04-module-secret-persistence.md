# Module Secret Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist each module's shared secret once in `platform_control` and load it on every startup, so module authentication is stable across process restarts and identical across instances (fixing the availability bug where provisioned firms' modules 403 after a restart / under horizontal scaling).

**Architecture:** Secret resolution moves from DI-wiring time (synchronous, no DB) to a startup `IHostedService` (`ModuleSecretResolver`) that get-or-creates each module's secret in `platform_control` and populates the in-process `ModuleCredential` (what the module sends) and the `ModuleRegistration` singleton (what `ModuleRegistrar` / firm provisioning write into control DBs). `AddModule` stops generating a per-boot random secret.

**Tech Stack:** .NET (C#), ASP.NET Core minimal APIs + hosted services, MongoDB driver, xUnit + EphemeralMongo (`SharedMongo`), `WebApplicationFactory<Program>`.

## Global Constraints

- Secrets persist in `platform_control` (the cross-firm, cross-instance registry) — one `moduleSecrets` document per module, keyed by module key. NOT per-firm.
- Get-or-create is idempotent and race-tolerant: an existing value is never overwritten; a concurrent duplicate-key insert is resolved by re-reading the winner.
- Modules register `Enabled = true` (installed ≠ entitled; the Phase 3b per-client `EnabledModules` gate is unchanged).
- `ModuleRegistrar` and `ProvisionFirm` are NOT modified — they already write the `ModuleRegistration` singletons, which now carry the persisted secret.
- The `ModuleSecretResolver` runs BEFORE `ModuleRegistrar` (register it in `AddLedgerEngine`, which precedes the `AddModule`-contributed `ModuleRegistrar`).
- No secret is ever logged or returned in a response. A still-empty secret fails authentication closed (no bypass).
- No rotation capability in this work (YAGNI — stabilize only).
- Stage explicit paths only (never `git add -A`); leave the pre-existing uncommitted noise (`*.Tests.csproj`, `environment.ts`, `.slnx`) alone. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## File Structure

**Create:**
- `Backend/Accounting101.Ledger.Api/Platform/ModuleSecret.cs` — the persisted secret document.
- `Backend/Accounting101.Ledger.Api/Hosting/ModuleSecretResolver.cs` — the startup resolver.
- `Backend/Accounting101.Ledger.Api.Tests/PlatformStoreModuleSecretTests.cs` — get-or-create unit tests.
- `Backend/Accounting101.Ledger.Api.Tests/ModuleSecretPersistenceTests.cs` — resolver stability + cross-restart regression.

**Modify:**
- `Backend/Accounting101.Ledger.Api/Platform/PlatformStore.cs` — add `moduleSecrets` collection + `GetOrCreateModuleSecretAsync`.
- `Backend/Accounting101.Ledger.Api/Auth/ModuleCredential.cs` — `record` → mutable class (settable `Secret`).
- `Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs` — `AddModule` stops generating; registers credential empty (keyed + unkeyed) + registration empty; remove the now-unused `Base64UrlEncode`.
- `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs` — register `ModuleSecretResolver`.

---

## Task 1: Persist module secrets in `platform_control`

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/ModuleSecret.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Platform/PlatformStore.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformStoreModuleSecretTests.cs` (create)

**Interfaces:**
- Produces: `ModuleSecret { string Key; string Secret; }` (BsonId on Key); `PlatformStore.GetOrCreateModuleSecretAsync(string key, Func<string> generate, CancellationToken) → Task<string>`.

- [ ] **Step 1: Write the failing tests.** Create `PlatformStoreModuleSecretTests.cs`:

```csharp
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformStoreModuleSecretTests
{
    private static async Task<PlatformStore> FreshStoreAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);
        return new PlatformStore(mongo.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Get_or_create_persists_a_generated_secret_and_returns_it()
    {
        PlatformStore store = await FreshStoreAsync();
        int calls = 0;
        string result = await store.GetOrCreateModuleSecretAsync("receivables", () => { calls++; return "SECRET-ONE"; });

        Assert.Equal("SECRET-ONE", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Get_or_create_is_idempotent_and_does_not_regenerate()
    {
        PlatformStore store = await FreshStoreAsync();
        string first = await store.GetOrCreateModuleSecretAsync("receivables", () => "FIRST");
        string second = await store.GetOrCreateModuleSecretAsync("receivables", () => "SHOULD-NOT-BE-USED");

        Assert.Equal("FIRST", first);
        Assert.Equal("FIRST", second); // the persisted value wins; generate() is not consulted
    }

    [Fact]
    public async Task Concurrent_get_or_create_for_one_key_converges_on_a_single_secret()
    {
        PlatformStore store = await FreshStoreAsync();
        int n = 0;
        string Gen() => "S" + Interlocked.Increment(ref n); // a distinct value each time it is actually called

        string[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => store.GetOrCreateModuleSecretAsync("cash", Gen)));

        Assert.Single(results.Distinct()); // all callers converge on ONE persisted secret, race notwithstanding
    }
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~PlatformStoreModuleSecretTests`
Expected: FAIL — `GetOrCreateModuleSecretAsync` does not exist (compile error).

- [ ] **Step 3: Create the `ModuleSecret` document.** Create `Backend/Accounting101.Ledger.Api/Platform/ModuleSecret.cs`:

```csharp
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// A module's persisted shared secret, stored once in platform_control so it is stable across process
/// restarts and identical across instances. Keyed by the module key. Never logged or surfaced to users.
/// </summary>
public sealed class ModuleSecret
{
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Add the collection + method to `PlatformStore`.** In `PlatformStore.cs`, add the field next to `_clusters`:

```csharp
    private readonly IMongoCollection<ModuleSecret> _moduleSecrets;
```

In the constructor, after `_clusters = ...`:

```csharp
        _moduleSecrets = database.GetCollection<ModuleSecret>("moduleSecrets");
```

And add the method (e.g. after `ListClustersAsync`):

```csharp
    /// <summary>Return the module's persisted secret, generating + persisting one on first use. Idempotent
    /// and race-tolerant: an existing value is returned unchanged, and a concurrent duplicate-key insert
    /// from another instance is resolved by re-reading the winner — so all instances converge on one
    /// stable secret. Never overwrites an existing secret.</summary>
    public async Task<string> GetOrCreateModuleSecretAsync(string key, Func<string> generate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generate);

        ModuleSecret? existing = await _moduleSecrets.Find(s => s.Key == key).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return existing.Secret;

        ModuleSecret created = new() { Key = key, Secret = generate() };
        try
        {
            await _moduleSecrets.InsertOneAsync(created, cancellationToken: cancellationToken);
            return created.Secret;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another instance persisted first — the _id (module key) is unique, so read the winner.
            ModuleSecret winner = await _moduleSecrets.Find(s => s.Key == key).FirstOrDefaultAsync(cancellationToken);
            return winner.Secret;
        }
    }
```

(`MongoWriteException` and `ServerErrorCategory` are in `MongoDB.Driver`, already imported in this file.)

- [ ] **Step 5: Run to verify pass.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~PlatformStoreModuleSecretTests`
Expected: PASS (3/3).

- [ ] **Step 6: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/ModuleSecret.cs Backend/Accounting101.Ledger.Api/Platform/PlatformStore.cs Backend/Accounting101.Ledger.Api.Tests/PlatformStoreModuleSecretTests.cs
git commit -m "feat(tenancy): persist module secrets in platform_control (get-or-create)"
```

---

## Task 2: Resolve persisted secrets at startup (the atomic wiring change)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Auth/ModuleCredential.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs`
- Create: `Backend/Accounting101.Ledger.Api/Hosting/ModuleSecretResolver.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleSecretPersistenceTests.cs` (create)

**Interfaces:**
- Consumes: `PlatformStore.GetOrCreateModuleSecretAsync` (Task 1); `ModuleRegistration` (`Control`); `ModuleCredential` (`Auth`); `PlatformStore` (`Platform`).
- Produces: `ModuleSecretResolver(IEnumerable<ModuleRegistration>, IEnumerable<ModuleCredential>, PlatformStore) : IHostedService`; `ModuleCredential(string key, string secret = "")` with a settable `Secret`.

> **Atomic:** the resolver, the `ModuleCredential` shape change, and the `AddModule` change must land together — until the resolver runs, `AddModule` no longer supplies a secret, so a host would have empty secrets. Existing module E2E suites stay green because both the sent credential and the stored registration are populated from the same persisted value before requests are served.

- [ ] **Step 1: Write the failing tests.** Create `ModuleSecretPersistenceTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ModuleSecretPersistenceTests
{
    [Fact]
    public async Task Resolver_loads_the_same_secret_on_a_second_boot()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);
        PlatformStore platform = new(mongo.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));

        // Boot 1 — fresh in-process singletons, as a process has at startup.
        ModuleRegistration reg1 = new() { Key = "receivables", Name = "Receivables", Enabled = true };
        ModuleCredential cred1 = new("receivables");
        await new ModuleSecretResolver([reg1], [cred1], platform).StartAsync(CancellationToken.None);

        Assert.NotEmpty(reg1.Secret);
        Assert.Equal(reg1.Secret, cred1.Secret); // registration + credential agree
        string bootOne = reg1.Secret;

        // Boot 2 — brand-new singletons (a "restart"), SAME platform DB.
        ModuleRegistration reg2 = new() { Key = "receivables", Name = "Receivables", Enabled = true };
        ModuleCredential cred2 = new("receivables");
        await new ModuleSecretResolver([reg2], [cred2], platform).StartAsync(CancellationToken.None);

        Assert.Equal(bootOne, reg2.Secret);   // stable across the restart — loaded, not regenerated
        Assert.Equal(bootOne, cred2.Secret);
    }

    [Fact]
    public async Task A_firm_provisioned_before_a_restart_keeps_valid_module_secrets()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string conn = runner.ConnectionString;
        IMongoClient mongo = new MongoClient(conn);
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");
        string controlDb = "control_" + Guid.NewGuid().ToString("N");

        WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.UseSetting("Mongo:ConnectionString", conn)
                 .UseSetting("Mongo:ControlDatabase", controlDb)
                 .UseSetting("Mongo:PlatformDatabase", platformDb));

        // Host 1: boot (resolver persists secrets, ModuleRegistrar seeds the default firm), then provision Firm B.
        string firmBControlDb;
        await using (WebApplicationFactory<Program> host1 = Build())
        {
            HttpClient op = host1.CreateClient();
            op.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                DevTokenDefaults.Scheme,
                DevToken.Encode(new DevTokenPayload(Guid.NewGuid(), "Operator", [new DevClaim("platform", "true")])));

            HttpResponseMessage created = await op.PostAsJsonAsync("/platform/firms", new ProvisionFirmRequest { Name = "Firm B" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            firmBControlDb = (await created.Content.ReadFromJsonAsync<FirmResponse>())!.ControlDatabase;
        }

        // The secret persisted under host 1 (read without regenerating — generate() must not be used).
        PlatformStore platform = new(mongo.GetDatabase(platformDb));
        string persisted = await platform.GetOrCreateModuleSecretAsync("receivables", () => "MUST-NOT-BE-USED");

        // Host 2: a restart — fresh process, SAME DBs. Its resolver must load the persisted secret.
        await using (WebApplicationFactory<Program> host2 = Build())
            _ = host2.CreateClient(); // force host boot

        // Firm B (provisioned under host 1) still holds the secret host 2 now uses → its modules authenticate.
        ControlStore firmBControl = new(mongo.GetDatabase(firmBControlDb));
        ModuleRegistration? firmBReceivables = await firmBControl.GetModuleAsync("receivables");
        Assert.NotNull(firmBReceivables);
        Assert.Equal(persisted, firmBReceivables!.Secret);

        // Host 2 did not regenerate: the persisted secret is unchanged after the restart.
        string afterRestart = await platform.GetOrCreateModuleSecretAsync("receivables", () => "MUST-NOT-BE-USED-2");
        Assert.Equal(persisted, afterRestart);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~ModuleSecretPersistenceTests`
Expected: FAIL — `ModuleSecretResolver` does not exist (compile error).

- [ ] **Step 3: Make `ModuleCredential` a mutable holder.** Replace the entire body of `Backend/Accounting101.Ledger.Api/Auth/ModuleCredential.cs`:

```csharp
namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// The in-process copy of a module's credential: the same key and secret the module sends as
/// <c>X-Module-Key</c> / <c>X-Module-Secret</c> when posting to the engine over HTTP. The
/// <see cref="Secret"/> is populated at startup by <see cref="Hosting.ModuleSecretResolver"/> from the
/// persisted <c>platform_control</c> value — so it is stable across restarts and identical across
/// instances. The module's <c>HttpLedgerClient</c> reads it per request (after startup completes).
/// </summary>
public sealed class ModuleCredential(string key, string secret = "")
{
    public string Key { get; } = key;
    public string Secret { get; set; } = secret;
}
```

(The two-arg positional form is preserved, so existing `new ModuleCredential("test-key", "test-secret")` call sites in the module `HttpLedgerClientTests` still compile. This changes the type from a `record` to a `class`; no code relies on its value-equality.)

- [ ] **Step 4: Stop generating in `AddModule`; register the credential empty (keyed + unkeyed).** In `ModuleHostingExtensions.cs`:

Remove `using System.Security.Cryptography;` from the top of the file (it becomes unused).

Replace the secret-generation block (currently lines ~22-25) — from the `// Generate a cryptographically random...` comment through `ModuleCredential credential = new(identity.Key, secret);` — with:

```csharp
        // The module's shared secret is resolved from platform_control at startup by ModuleSecretResolver
        // (persist-once, load-thereafter — stable across restarts + instances). Register the credential and
        // the control-DB registration with an empty secret now; the resolver fills both before requests run.
        ModuleCredential credential = new(identity.Key);
```

Change the credential registration (currently `services.AddKeyedSingleton<ModuleCredential>(identity.Key, credential);`) to register the SAME instance both keyed and unkeyed:

```csharp
        // Keyed for each module's HttpLedgerClient; also unkeyed so ModuleSecretResolver can enumerate every
        // credential at startup and populate its secret. Same instance both ways — one object to mutate.
        services.AddKeyedSingleton<ModuleCredential>(identity.Key, credential);
        services.AddSingleton(credential);
```

Change the `ModuleRegistration` registration to omit the secret (defaults to empty; the resolver fills it):

```csharp
        services.AddSingleton(new ModuleRegistration
        {
            Key = identity.Key,
            Name = name,
            Enabled = true,
        });
```

Remove the now-unused `Base64UrlEncode` helper (the `private static string Base64UrlEncode(byte[] bytes) => ...` method at the bottom of the class).

- [ ] **Step 5: Create the resolver.** Create `Backend/Accounting101.Ledger.Api/Hosting/ModuleSecretResolver.cs`:

```csharp
using System.Security.Cryptography;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// On startup, resolves each installed module's shared secret from <c>platform_control</c> (generating +
/// persisting one on first use) and populates the in-process <see cref="ModuleCredential"/> (what the
/// module sends) and the <see cref="ModuleRegistration"/> singleton (what <see cref="ModuleRegistrar"/> and
/// firm provisioning write into control DBs). Registered before <see cref="ModuleRegistrar"/> so the
/// registrations it seeds carry the persisted secret. Because the secret is persisted once and loaded
/// thereafter, it is stable across restarts and identical across instances — so a module authenticates
/// against any firm's control DB regardless of which process seeded it. The secret is never logged.
/// </summary>
public sealed class ModuleSecretResolver(
    IEnumerable<ModuleRegistration> registrations,
    IEnumerable<ModuleCredential> credentials,
    PlatformStore platform) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, ModuleCredential> credentialByKey = credentials.ToDictionary(c => c.Key);
        foreach (ModuleRegistration registration in registrations)
        {
            string secret = await platform.GetOrCreateModuleSecretAsync(registration.Key, GenerateSecret, cancellationToken);
            registration.Secret = secret;
            if (credentialByKey.TryGetValue(registration.Key, out ModuleCredential? credential))
                credential.Secret = secret;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The only place a module secret is minted: 32 cryptographically random bytes → Base64URL (no padding).
    private static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 6: Register the resolver before `ModuleRegistrar`.** In `LedgerEngineExtensions.cs`, immediately after `services.AddPlatformRegistry(configuration);`:

```csharp
        // Resolve each installed module's shared secret from platform_control at startup (persist-once,
        // load-thereafter) and populate the in-process credentials + registrations BEFORE ModuleRegistrar
        // seeds the default firm — so secrets are stable across restarts and identical across instances.
        services.AddHostedService<ModuleSecretResolver>();
```

(`AddLedgerEngine` runs before any `AddModule` in the host's composition root, so this `IHostedService` is registered — and therefore starts — before the `ModuleRegistrar` that `AddModule` contributes. The resolver only touches `platform_control`, so it does not depend on the default firm existing.)

- [ ] **Step 7: Run the focused tests.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~ModuleSecretPersistenceTests`
Expected: PASS (2/2).

- [ ] **Step 8: Run the full Api.Tests project (the resolver runs on every host boot; module E2E must stay green).**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: all PASS (was 333/333; this adds 5 across the two new files). If `ModuleHostingTests` or a module-posting E2E fails, confirm the resolver is registered before `ModuleRegistrar` and that the credential is registered both keyed and unkeyed.

- [ ] **Step 9: Run the module suites (they boot the full host through the resolver + real module auth).**

Run each:
`dotnet test Modules/Receivables/Accounting101.Receivables.Tests`
`dotnet test Modules/Payables/Accounting101.Payables.Tests`
`dotnet test Modules/Payroll/Accounting101.Payroll.Tests`
`dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests`
`dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: all PASS (module-originated posts still authenticate — sent secret and stored secret both come from the resolver).

- [ ] **Step 10: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Auth/ModuleCredential.cs Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs Backend/Accounting101.Ledger.Api/Hosting/ModuleSecretResolver.cs Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs Backend/Accounting101.Ledger.Api.Tests/ModuleSecretPersistenceTests.cs
git commit -m "feat(tenancy): resolve persisted module secrets at startup (stable across restarts)"
```

---

## Self-Review

**Spec coverage:**
- `ModuleSecret` doc + `moduleSecrets` collection → Task 1. ✓
- `PlatformStore.GetOrCreateModuleSecretAsync` (atomic, race-tolerant) → Task 1. ✓
- `ModuleSecretResolver` (before `ModuleRegistrar`, populates credential + registration) → Task 2, Steps 5-6. ✓
- `AddModule` stops generating; empty credential/registration → Task 2, Step 4. ✓
- `ModuleCredential` mutable → Task 2, Step 3. ✓
- `ModuleRegistrar` / `ProvisionFirm` unchanged → not touched (verified: neither appears in any Modify list). ✓
- Testing: get-or-create idempotence/race (Task 1); reboot stability + cross-restart provisioned-firm regression (Task 2). ✓
- Fail-closed on empty secret: covered by the unchanged constant-time comparison in `CredentialModuleAuthenticator` (an empty stored/sent secret never matches a real one) — asserted implicitly by the full suite staying green; no new code needed.

**Placeholder scan:** none — every step has concrete code/commands.

**Type consistency:** `GetOrCreateModuleSecretAsync(string, Func<string>, CancellationToken) → Task<string>`, `ModuleSecret { Key, Secret }`, `ModuleCredential(string key, string secret = "")` with settable `Secret`, `ModuleSecretResolver(IEnumerable<ModuleRegistration>, IEnumerable<ModuleCredential>, PlatformStore)` — used consistently across tasks and tests. ✓
