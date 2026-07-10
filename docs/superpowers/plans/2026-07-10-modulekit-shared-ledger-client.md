# ModuleKit — Shared Module→Ledger Client & Relay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the duplicated module→ledger surface (typed exception, relay helpers, ledger-truth resolver, HTTP client plumbing) into two shared `ModuleKit` assemblies, collapse each module's client to a ctor-only subclass, and centralize the error relay in one host middleware that closes the write-path 500→4xx gap.

**Architecture:** `Accounting101.ModuleKit` (domain-safe: `LedgerClientException` + `LedgerTruth`) and `Accounting101.ModuleKit.Api` (`ModuleLedgerClient` base + relay middleware + DI extension). Each module keeps its narrow per-module `ILedgerClient` interface; its concrete `HttpLedgerClient` becomes a ctor-only subclass of the base (sub-choice 2b). A single `UseModuleLedgerExceptionRelay` middleware relays every escaping `LedgerClientException` as `problem+json`.

**Tech Stack:** C# / .NET 10 minimal-API modular monolith, xUnit + EphemeralMongo host fixtures, `Accounting101.slnx`.

## Global Constraints

- **2b subclass, keep the class name.** Each module's concrete client stays named `HttpLedgerClient` in its own `.Api` namespace (namespaces disambiguate; existing `HttpLedgerClientTests` keep compiling). It becomes: `public sealed class HttpLedgerClient(HttpClient http, IHttpContextAccessor context, [FromKeyedServices("<key>")] ModuleCredential credential) : ModuleLedgerClient(http, context, credential), ILedgerClient;` — no method bodies.
- **Narrow per-module `ILedgerClient` interfaces are unchanged.** Do not touch them. Inherited public base methods satisfy them.
- **Base methods are byte-faithful ports** of the current AR `HttpLedgerClient` (Post/Approve/Reverse/Void/Validate/GetEntriesBySourceRef/GetSubledger) + Inventory's `GetEntriesBySourceRefs`. Credential (`X-Module-Key`/`X-Module-Secret`) attaches on `PostAsync`, `ReverseAsync`, `VoidAsync`, `ValidateAsync` only; `ApproveAsync` and all reads do not attach it.
- **The relay is one home.** After a module migrates to the base, delete its per-endpoint `catch (LedgerClientException)` arms — the middleware owns the relay. Keep sibling `catch (InvalidOperationException)` / other arms. If removing the `LedgerClientException` arm would leave a `try` with no remaining `catch`/`finally`, remove the whole `try` wrapper (this is the case for the FA/Inventory read endpoints, whose try/catch was added by the fold-on-read hardening and has only the `LedgerClientException` arm).
- **Engine untouched.** No file under `Backend/` changes except nothing — ModuleKit references the engine, never the reverse.
- **`problem+json` shape matches the host convention** (`Program.cs:46`): `Status = ex.StatusCode`, `Detail = ex.Reason`, `contentType: "application/problem+json"`.
- **Ordering (green at every commit):** Task 1 builds ModuleKit + registers the middleware in the host FIRST (inert — nothing throws `ModuleKit.LedgerClientException` yet). Then each module migrates in its own task, swapping its client to the base and deleting its catches in the same commit.
- Branch `refactor/modulekit-shared-ledger-client` off master. Commit per task.
- Reference source to copy verbatim: AR `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`, `Modules/Receivables/Accounting101.Receivables/LedgerClientException.cs`, `Modules/Banking/Cash/Accounting101.Banking.Cash/CashLedgerStatus.cs`, `Modules/Receivables/Accounting101.Receivables.Tests/HttpLedgerClientTests.cs`.

## File Structure

- **Create:** `Modules/Shared/Accounting101.ModuleKit/` — `Accounting101.ModuleKit.csproj`, `LedgerClientException.cs`, `LedgerTruth.cs`
- **Create:** `Modules/Shared/Accounting101.ModuleKit.Api/` — `Accounting101.ModuleKit.Api.csproj`, `ModuleLedgerClient.cs`, `ModuleLedgerExceptionRelayExtensions.cs`, `ModuleLedgerClientServiceExtensions.cs`
- **Create:** `Modules/Shared/Accounting101.ModuleKit.Tests/` — `Accounting101.ModuleKit.Tests.csproj`, `LedgerTruthTests.cs`, `ModuleLedgerClientTests.cs`
- **Modify:** `Accounting101.slnx` (add 3 projects); `Accounting101.Host/Accounting101.Host.csproj` (+= ModuleKit.Api ref); `Accounting101.Host/Program.cs` (register middleware)
- **Modify (per module, Tasks 2–8):** each module's `HttpLedgerClient.cs`, `*ServiceExtensions.cs`, `.Api.csproj`; delete each module's `LedgerClientException.cs` (5 modules) or `*LedgerStatus.cs` (Cash/Payroll); Cash/Payroll domain `.csproj` + service; AR/AP `HttpLedgerClientTests.cs` namespace import; endpoint catch-arm deletions.

---

### Task 1: ModuleKit assemblies, relay middleware, host wiring, unit tests

**Files:**
- Create: `Modules/Shared/Accounting101.ModuleKit/Accounting101.ModuleKit.csproj`
- Create: `Modules/Shared/Accounting101.ModuleKit/LedgerClientException.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit/LedgerTruth.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit.Api/Accounting101.ModuleKit.Api.csproj`
- Create: `Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClient.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerExceptionRelayExtensions.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClientServiceExtensions.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit.Tests/Accounting101.ModuleKit.Tests.csproj`
- Create: `Modules/Shared/Accounting101.ModuleKit.Tests/LedgerTruthTests.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit.Tests/ModuleLedgerClientTests.cs`
- Modify: `Accounting101.slnx`, `Accounting101.Host/Accounting101.Host.csproj`, `Accounting101.Host/Program.cs`

**Interfaces:**
- Produces: `Accounting101.ModuleKit.LedgerClientException(int, string)` with `int StatusCode`, `string Reason`; `Accounting101.ModuleKit.LedgerTruth.ShowsVoided(IReadOnlyList<EntryResponse>)`; `abstract Accounting101.ModuleKit.Api.ModuleLedgerClient(HttpClient, IHttpContextAccessor, ModuleCredential)` with public `PostAsync/ApproveAsync/ReverseAsync/VoidAsync/ValidateAsync/GetEntriesBySourceRefAsync/GetEntriesBySourceRefsAsync/GetSubledgerAsync`; `IApplicationBuilder.UseModuleLedgerExceptionRelay()`; `IServiceCollection.AddModuleLedgerClient<TInterface,TClient>(string httpClientName, IConfiguration)`.

- [ ] **Step 1: Write the failing unit tests**

Create `Modules/Shared/Accounting101.ModuleKit.Tests/Accounting101.ModuleKit.Tests.csproj` (mirror another module `.Tests.csproj` for the exact xunit/coverlet package versions and `<IsPackable>false</IsPackable>`; references below):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <!-- xunit + Microsoft.NET.Test.Sdk + xunit.runner.visualstudio PackageReferences: copy versions from a sibling *.Tests.csproj -->
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Accounting101.ModuleKit\Accounting101.ModuleKit.csproj" />
    <ProjectReference Include="..\Accounting101.ModuleKit.Api\Accounting101.ModuleKit.Api.csproj" />
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Api\Accounting101.Ledger.Api.csproj" />
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Contracts\Accounting101.Ledger.Contracts.csproj" />
  </ItemGroup>
</Project>
```

Create `Modules/Shared/Accounting101.ModuleKit.Tests/LedgerTruthTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public sealed class LedgerTruthTests
{
    private static EntryResponse Entry(Guid id, string status, Guid? reversalOf = null) =>
        new(Id: id, SequenceNumber: 0, EffectiveDate: default, Type: "Standard", Status: status,
            Posting: "Posted", LineCount: 0, Supersedes: null, SupersededBy: null, ReversalOf: reversalOf,
            ReversedBy: null, Lines: []);

    [Fact]
    public void No_primary_entry_falls_back_to_envelope_returns_false() =>
        Assert.False(LedgerTruth.ShowsVoided([]));

    [Fact]
    public void Primary_withdrawn_while_pending_shows_voided()
    {
        EntryResponse primary = Entry(Guid.NewGuid(), "Voided");
        Assert.True(LedgerTruth.ShowsVoided([primary]));
    }

    [Fact]
    public void Reversal_of_a_primary_shows_voided()
    {
        Guid primaryId = Guid.NewGuid();
        EntryResponse primary = Entry(primaryId, "Active");
        EntryResponse reversal = Entry(Guid.NewGuid(), "Active", reversalOf: primaryId);
        Assert.True(LedgerTruth.ShowsVoided([primary, reversal]));
    }

    [Fact]
    public void Clean_active_primary_is_not_voided()
    {
        EntryResponse primary = Entry(Guid.NewGuid(), "Active");
        Assert.False(LedgerTruth.ShowsVoided([primary]));
    }
}
```

Create `Modules/Shared/Accounting101.ModuleKit.Tests/ModuleLedgerClientTests.cs` by copying `Modules/Receivables/Accounting101.Receivables.Tests/HttpLedgerClientTests.cs` and adapting: (a) namespace `Accounting101.ModuleKit.Tests`; (b) `using Accounting101.ModuleKit;` + `using Accounting101.ModuleKit.Api;` replacing `using Accounting101.Receivables.Api;`; (c) because `ModuleLedgerClient` is abstract, define a trivial concrete test double at the top of the file and instantiate *it* instead of `HttpLedgerClient`:

```csharp
private sealed class TestLedgerClient(HttpClient http, IHttpContextAccessor context, ModuleCredential credential)
    : ModuleLedgerClient(http, context, credential);
```

Keep the six AR test methods verbatim (Post forwards auth; Post throws typed exception on 409; GetEntriesBySourceRef builds query; Void forwards auth; Validate returns on 200; Validate throws on 409; Validate surfaces 422 field-level text) — they exercise `Forwarded`, credential attach, `EnsureSuccessAsync`, and `ReasonFrom` on the base. Replace each `new HttpLedgerClient(...)` with `new TestLedgerClient(...)`.

- [ ] **Step 2: Run the tests to verify they fail (no assemblies yet)**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests`
Expected: FAIL to compile / restore — the ModuleKit assemblies don't exist yet.

- [ ] **Step 3: Create `Accounting101.ModuleKit` (domain-safe)**

`Modules/Shared/Accounting101.ModuleKit/Accounting101.ModuleKit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Contracts\Accounting101.Ledger.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`LedgerClientException.cs` — copy `Modules/Receivables/Accounting101.Receivables/LedgerClientException.cs` verbatim, change the namespace to `Accounting101.ModuleKit`.

`LedgerTruth.cs` — copy the body of `Modules/Banking/Cash/Accounting101.Banking.Cash/CashLedgerStatus.cs`, rename the class to `LedgerTruth`, namespace `Accounting101.ModuleKit`, keep the `ShowsVoided` method verbatim:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.ModuleKit;

/// <summary>
/// Ledger-truth for a module document's Void state, read from its source journal entries. A document is
/// negated when its <em>primary</em> entry (the one that is not a reversal) has been withdrawn while
/// pending (engine sets its <c>Status</c> to <c>Voided</c>), or when a reversal of that primary exists
/// (<c>ReversalOf</c> points at it). The single shared home of the rule; a service unions it with the
/// document-envelope status so a read can only ever be <em>promoted</em> to Void, never demoted.
/// </summary>
public static class LedgerTruth
{
    public static bool ShowsVoided(IReadOnlyList<EntryResponse> entriesForOneDoc)
    {
        List<EntryResponse> primaries = entriesForOneDoc.Where(e => e.ReversalOf is null).ToList();
        if (primaries.Count == 0) return false;                       // no entry → fall back to envelope
        if (primaries.Any(p => p.Status == "Voided")) return true;    // withdrawn while pending
        HashSet<Guid> primaryIds = primaries.Select(p => p.Id).ToHashSet();
        return entriesForOneDoc.Any(e => e.ReversalOf is { } r && primaryIds.Contains(r)); // reversed
    }
}
```

- [ ] **Step 4: Create `Accounting101.ModuleKit.Api`**

`Accounting101.ModuleKit.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Accounting101.ModuleKit\Accounting101.ModuleKit.csproj" />
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Api\Accounting101.Ledger.Api.csproj" />
  </ItemGroup>
</Project>
```

`ModuleLedgerClient.cs` — the abstract base. Port the method bodies from AR's `HttpLedgerClient.cs` (Post/Approve/Reverse/Void/Validate/GetEntriesBySourceRef/GetSubledger) plus `GetEntriesBySourceRefsAsync` from Inventory's client, and the `Forwarded`/`EnsureSuccessAsync`/`ReasonFrom` helpers from AR (verbatim):

```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Http;

namespace Accounting101.ModuleKit.Api;

/// <summary>
/// The shared base for every module's ledger client: a typed HttpClient onto the engine's ledger
/// endpoints. It forwards the caller's Authorization header (so the engine authenticates the same user
/// and applies its full policy), attaches the owning module's credential on write operations (so the
/// engine authorizes under the module path and stamps <c>ViaModule</c>), and relays any non-2xx as a
/// typed <see cref="LedgerClientException"/>. Each module derives a thin concrete client that supplies
/// its keyed <see cref="ModuleCredential"/> and implements its own narrow ledger-client interface; the
/// public members here satisfy that interface.
/// </summary>
public abstract class ModuleLedgerClient(HttpClient http, IHttpContextAccessor context, ModuleCredential credential)
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        WithModuleCredential(request);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PostEntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ApproveAsync(Guid clientId, Guid entryId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/approve");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/reverse");
        WithModuleCredential(message);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/void");
        WithModuleCredential(message);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/validate");
        WithModuleCredential(request);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRef={sourceRef}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(
        Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        if (sourceRefs.Count == 0) return [];
        string csv = string.Join(',', sourceRefs);
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRefs={csv}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        string url = $"clients/{clientId}/subledger?account={account}&dimension={Uri.EscapeDataString(dimension)}";
        if (asOf is { } d) url += $"&asOf={d:yyyy-MM-dd}";
        if (includePending) url += "&includePending=true";
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, url);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        SubledgerResponse body = (await response.Content.ReadFromJsonAsync<SubledgerResponse>(cancellationToken))!;
        return body.Lines;
    }

    private void WithModuleCredential(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
    }

    private HttpRequestMessage Forwarded(HttpMethod method, string uri)
    {
        HttpRequestMessage request = new(method, uri);
        string? authorization = context.HttpContext?.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

    /// <summary>
    /// Pull the best available reason from the response body.
    /// Priority: (1) <c>errors</c> map from ValidationProblemDetails (flattened to <c>"field: msg; …"</c>),
    /// (2) ProblemDetails <c>detail</c>, (3) raw body, (4) status phrase.
    /// </summary>
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
                            {
                                sb.Append(string.Join(", ", prop.Value.EnumerateArray()
                                    .Select(m => m.GetString() ?? string.Empty)));
                            }
                            else
                            {
                                sb.Append(prop.Value.GetRawText().Trim('"'));
                            }
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
}
```

> **Implementer:** `SubledgerResponse` (used by `GetSubledgerAsync`) is in `Accounting101.Ledger.Contracts`. The `ReasonFrom`/`EnsureSuccessAsync`/method bodies above are byte-identical to the current AR `HttpLedgerClient.cs`; you can diff against it to confirm faithfulness.

`ModuleLedgerExceptionRelayExtensions.cs`:

```csharp
using Accounting101.ModuleKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.ModuleKit.Api;

/// <summary>
/// Registers the single relay that translates any escaping <see cref="LedgerClientException"/> into a
/// <c>application/problem+json</c> response carrying the engine's own status and reason — so a ledger
/// refusal never escapes a module endpoint as an opaque 500. Genuine engine 500s relay as 500 (honest);
/// only a clean 4xx that would otherwise be swallowed is straightened out.
/// </summary>
public static class ModuleLedgerExceptionRelayExtensions
{
    public static IApplicationBuilder UseModuleLedgerExceptionRelay(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (LedgerClientException ex)
            {
                if (ctx.Response.HasStarted) throw; // headers already sent — cannot reshape
                ctx.Response.StatusCode = ex.StatusCode;
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = ex.StatusCode,
                    Title = "Ledger request failed",
                    Detail = ex.Reason,
                }, options: null, contentType: "application/problem+json", cancellationToken: ctx.RequestAborted);
            }
        });
}
```

`ModuleLedgerClientServiceExtensions.cs`:

```csharp
using Accounting101.ModuleKit.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.ModuleKit.Api;

public static class ModuleLedgerClientServiceExtensions
{
    /// <summary>
    /// Registers a module's named loopback ledger HttpClient (based at <c>Engine:BaseAddress</c>) and
    /// binds it to the module's typed client. The explicit <paramref name="httpClientName"/> avoids the
    /// ILedgerClient short-name collision across modules. The keyed <c>ModuleCredential</c> is registered
    /// separately by the module's <c>AddModule</c> call and resolved via <c>[FromKeyedServices]</c>.
    /// </summary>
    public static IServiceCollection AddModuleLedgerClient<TInterface, TClient>(
        this IServiceCollection services, string httpClientName, IConfiguration configuration)
        where TInterface : class
        where TClient : ModuleLedgerClient, TInterface
    {
        services.AddHttpClient(httpClientName, client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<TInterface, TClient>();
        return services;
    }
}
```

- [ ] **Step 5: Add the three projects to the solution and wire the host**

Add all three projects to `Accounting101.slnx` (mirror how existing module projects are listed — a `<Project Path="Modules/Shared/Accounting101.ModuleKit/Accounting101.ModuleKit.csproj" />` entry per project, in whatever grouping the file uses).

`Accounting101.Host/Accounting101.Host.csproj` — add `<ProjectReference Include="..\Modules\Shared\Accounting101.ModuleKit.Api\Accounting101.ModuleKit.Api.csproj" />`.

`Accounting101.Host/Program.cs` — add `using Accounting101.ModuleKit.Api;` and register the middleware immediately after the existing inline exception middleware block (after the closing `});` at line 82, before `if (app.Environment.IsDevelopment())`):

```csharp
// Relay any escaping module ledger-client refusal (LedgerClientException) as problem+json carrying the
// engine's real status + reason — the single home of the module→ledger error relay.
app.UseModuleLedgerExceptionRelay();
```

- [ ] **Step 6: Run the ModuleKit tests to verify they pass**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests`
Expected: PASS — LedgerTruth truth table (4) + ported client tests (7) all green.

- [ ] **Step 7: Build the whole solution (host wiring compiles, nothing regressed)**

Run: `dotnet build Accounting101.slnx -m:1`
Expected: SUCCESS — the host references ModuleKit.Api and registers the inert middleware; no module migrated yet.

- [ ] **Step 8: Commit**

```bash
git add Modules/Shared Accounting101.slnx Accounting101.Host/Accounting101.Host.csproj Accounting101.Host/Program.cs
git commit -m "feat(modulekit): shared ledger client base, LedgerTruth, relay middleware + host wiring"
```

---

### Task 2: Migrate Cash (pattern-setter — resolver + client + write-path relay E2E)

**Files:**
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/HttpLedgerClient.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashServiceExtensions.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/Accounting101.Banking.Cash.Api.csproj`
- Delete: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashLedgerStatus.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/Accounting101.Banking.Cash.csproj`
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/LedgerErrorRelayE2eTests.cs` (new)

**Interfaces:**
- Consumes: `ModuleLedgerClient`, `LedgerTruth`, `AddModuleLedgerClient`, `UseModuleLedgerExceptionRelay` (Task 1).

- [ ] **Step 1: Write the failing write-path relay E2E**

Create `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/LedgerErrorRelayE2eTests.cs` — a cash *write* the engine refuses returns a relayed 4xx, not 500. Mirror the AR `LedgerErrorRelayE2eTests` closed-period void scenario, adapted to Cash's fixture and endpoints. Use the Cash host fixture's SoD/seed helpers and post a disbursement, approve it, close the period it's dated in, then void it and assert the response is not 500, is in `[400,499]`, and carries a non-empty `ProblemDetails.Detail`.

> **Implementer:** read `Modules/Receivables/Accounting101.Receivables.Tests/LedgerErrorRelayE2eTests.cs` for the shape and `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashHostFixture.cs` for the exact seed/post/close/void helpers and request DTOs. The assertion block is:
> ```csharp
> Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
> Assert.InRange((int)resp.StatusCode, 400, 499);
> ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
> Assert.NotNull(problem);
> Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
> ```

- [ ] **Step 2: Run it to verify it fails (proves the pre-migration 500)**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter LedgerErrorRelayE2eTests`
Expected: FAIL — Cash's write path currently uses bare `EnsureSuccessStatusCode` and no relay, so the void returns 500.

- [ ] **Step 3: Swap the Cash client to the base**

Replace the entire body of `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/HttpLedgerClient.cs` with the 2b subclass (keep the class name and namespace):

```csharp
using Accounting101.Ledger.Api.Auth;
using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Banking.Cash.Api;

/// <summary>Cash's ledger client — a thin subclass of the shared <see cref="ModuleLedgerClient"/> base
/// that supplies the keyed cash module credential and implements <see cref="ILedgerClient"/> (its public
/// base members satisfy the interface).</summary>
public sealed class HttpLedgerClient(
    HttpClient http, IHttpContextAccessor context,
    [FromKeyedServices("cash")] ModuleCredential credential)
    : ModuleLedgerClient(http, context, credential), ILedgerClient;
```

- [ ] **Step 4: Point the resolver at `LedgerTruth` and delete `CashLedgerStatus`**

In `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs`, replace every `CashLedgerStatus.ShowsVoided(` with `LedgerTruth.ShowsVoided(` and add `using Accounting101.ModuleKit;`. Delete `Modules/Banking/Cash/Accounting101.Banking.Cash/CashLedgerStatus.cs`.

- [ ] **Step 5: Adopt `AddModuleLedgerClient` and add project references**

In `CashServiceExtensions.cs`, replace the `AddHttpClient("CashLedgerClient", …).AddTypedClient<ILedgerClient, HttpLedgerClient>()` block with:

```csharp
services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("CashLedgerClient", configuration);
```

Add `using Accounting101.ModuleKit.Api;`. Add to `Accounting101.Banking.Cash.Api.csproj` a `ProjectReference` to `Accounting101.ModuleKit.Api`, and to `Accounting101.Banking.Cash.csproj` (domain) a `ProjectReference` to `Accounting101.ModuleKit`.

- [ ] **Step 6: Run the E2E to verify it passes, then the full Cash suite**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests`
Expected: PASS — the new relay E2E now returns a relayed 4xx (middleware from Task 1), and all pre-existing Cash tests (status-resolver reads via `LedgerTruth`, client behavior via the base) stay green.

- [ ] **Step 7: Commit**

```bash
git add Modules/Banking/Cash
git commit -m "refactor(cash): adopt ModuleKit base + LedgerTruth; relay closes write-path 500"
```

---

### Task 3: Migrate Payroll (resolver + client)

**Files:**
- Modify: `Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs`, `.../PayrollServiceExtensions.cs` (or the file with `AddPayroll`), `.../Accounting101.Payroll.Api.csproj`
- Delete: `Modules/Payroll/Accounting101.Payroll/PayrollLedgerStatus.cs`
- Modify: `Modules/Payroll/Accounting101.Payroll/PayrollService.cs`, `.../Accounting101.Payroll.csproj`

**Interfaces:** Consumes Task 1. Same shape as Task 2 minus the new E2E.

- [ ] **Step 1: Swap the Payroll client to the base**

Replace `Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs` body with the 2b subclass, key `"payroll"`, interface `ILedgerClient` (namespace `Accounting101.Payroll.Api`) — identical structure to Task 2 Step 3.

- [ ] **Step 2: Point resolver at `LedgerTruth`, delete `PayrollLedgerStatus`**

In `PayrollService.cs` replace `PayrollLedgerStatus.ShowsVoided(` → `LedgerTruth.ShowsVoided(`, add `using Accounting101.ModuleKit;`. Delete `Modules/Payroll/Accounting101.Payroll/PayrollLedgerStatus.cs`.

- [ ] **Step 3: Adopt `AddModuleLedgerClient` + project references**

In the Payroll service-extensions file, replace the `AddHttpClient("PayrollLedgerClient", …).AddTypedClient<…>()` block with `services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("PayrollLedgerClient", configuration);` (verify the exact HttpClient name string in the current file and reuse it), add `using Accounting101.ModuleKit.Api;`. Add `ModuleKit.Api` ref to `Accounting101.Payroll.Api.csproj` and `ModuleKit` ref to `Accounting101.Payroll.csproj`.

- [ ] **Step 4: Run the full Payroll suite**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests`
Expected: PASS — status reads via `LedgerTruth`, client via base; no regressions.

- [ ] **Step 5: Commit**

```bash
git add Modules/Payroll
git commit -m "refactor(payroll): adopt ModuleKit base + LedgerTruth"
```

---

### Task 4: Migrate Receivables (client + delete exception + delete 11 catch arms)

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`, `.../ReceivablesEndpoints.cs`, the AR service-extensions file, `.../Accounting101.Receivables.Api.csproj`
- Delete: `Modules/Receivables/Accounting101.Receivables/LedgerClientException.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/HttpLedgerClientTests.cs` (namespace import)

**Interfaces:** Consumes Task 1.

- [ ] **Step 1: Swap the AR client to the base**

Replace `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs` body with the 2b subclass, key `"receivables"`, interface `ILedgerClient` (namespace `Accounting101.Receivables.Api`). (AR's interface includes `ApproveAsync`/`ValidateAsync`/`GetSubledgerAsync` — all satisfied by inherited base members.)

- [ ] **Step 2: Delete `LedgerClientException.cs` and re-point its references**

Delete `Modules/Receivables/Accounting101.Receivables/LedgerClientException.cs`. In `Modules/Receivables/Accounting101.Receivables.Tests/HttpLedgerClientTests.cs`, replace `using Accounting101.Receivables.Api;` usage of `LedgerClientException` by adding `using Accounting101.ModuleKit;` (the type moved; `Assert.ThrowsAsync<LedgerClientException>` now resolves from ModuleKit). Also update the test's `new HttpLedgerClient(...)` calls: they still compile (the class name is unchanged and the ctor signature `(HttpClient, IHttpContextAccessor, ModuleCredential)` still binds — the `[FromKeyedServices]` attribute is ignored by a direct `new`).

- [ ] **Step 3: Delete the 11 `catch (LedgerClientException)` arms**

In `ReceivablesEndpoints.cs`, remove each `catch (LedgerClientException ex) { return Results.Problem(ex.Reason, statusCode: ex.StatusCode); }` block (11 of them). Keep every sibling `catch (InvalidOperationException …)` arm intact. Each affected handler retains its `try` + the `InvalidOperationException` catch, so no empty `try` results. Remove the now-unused `using` for the old exception if present (the type was in the AR namespace, likely no explicit using).

- [ ] **Step 4: Adopt `AddModuleLedgerClient` + project reference**

In the AR service-extensions file, replace the `AddHttpClient("ReceivablesLedgerClient", …).AddTypedClient<…>()` block with `services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("ReceivablesLedgerClient", configuration);` (verify the exact name string), add `using Accounting101.ModuleKit.Api;`. Add `ModuleKit.Api` ref to `Accounting101.Receivables.Api.csproj`. (AR domain project needs no ModuleKit ref — `InvoiceService` only mentions the exception in comments.)

- [ ] **Step 5: Run the full AR suite**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests`
Expected: PASS — `HttpLedgerClientTests` green against the base via the concrete client; `LedgerErrorRelayE2eTests` green via the middleware (not the deleted per-endpoint catch); all settlement/invoice suites unchanged.

- [ ] **Step 6: Commit**

```bash
git add Modules/Receivables
git commit -m "refactor(receivables): adopt ModuleKit base; relay via host middleware, drop per-endpoint catches"
```

---

### Task 5: Migrate Payables (client + delete exception + delete 5 catch arms)

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`, `.../PayablesEndpoints.cs`, the AP service-extensions file, `.../Accounting101.Payables.Api.csproj`
- Delete: `Modules/Payables/Accounting101.Payables/LedgerClientException.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs` (namespace import, if present)

**Interfaces:** Consumes Task 1.

- [ ] **Step 1: Swap the AP client to the base** — 2b subclass, key `"payables"`, interface `ILedgerClient` (namespace `Accounting101.Payables.Api`). Same structure as Task 4 Step 1.

- [ ] **Step 2: Delete `LedgerClientException.cs` and re-point references** — delete the file; in `Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs` (if it references the type) add `using Accounting101.ModuleKit;`. Also check `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs` — if the fake references or throws `LedgerClientException`, add the ModuleKit using.

- [ ] **Step 3: Delete the 5 `catch (LedgerClientException)` arms** in `PayablesEndpoints.cs`; keep sibling `catch (InvalidOperationException)` arms.

- [ ] **Step 4: Adopt `AddModuleLedgerClient` + `ModuleKit.Api` ref** — replace the AP `AddHttpClient(...).AddTypedClient(...)` with `services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("PayablesLedgerClient", configuration);` (verify the name), add usings + `.Api.csproj` reference.

- [ ] **Step 5: Run the full AP suite**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: PASS — client, relay E2E (via middleware), bill/settlement suites all green.

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables
git commit -m "refactor(payables): adopt ModuleKit base; relay via host middleware, drop per-endpoint catches"
```

---

### Task 6: Migrate Reconciliation (client + delete exception + delete 2 catch arms)

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/HttpLedgerClient.cs`, `.../ReconciliationEndpoints.cs`, the Reconciliation service-extensions file, `.../Accounting101.Banking.Reconciliation.Api.csproj`
- Delete: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/LedgerClientException.cs`
- Modify: any Reconciliation test referencing `LedgerClientException` (namespace import)

**Interfaces:** Consumes Task 1.

- [ ] **Step 1: Swap the Reconciliation client to the base** — 2b subclass, key `"reconciliation"` (verify against the current `[FromKeyedServices(...)]` key in the file — the module key per memory is `bankrec`/`reconciliation`; use exactly what the current client uses), interface `ILedgerClient` in namespace `Accounting101.Banking.Reconciliation.Api`.

- [ ] **Step 2: Delete `LedgerClientException.cs`; re-point any test reference** with `using Accounting101.ModuleKit;`.

- [ ] **Step 3: Delete the 2 `catch (LedgerClientException)` arms** in `ReconciliationEndpoints.cs`. If a sibling `catch` remains, keep it; if removing the arm leaves an empty `try`, remove the `try` wrapper (verify each of the 2 sites).

- [ ] **Step 4: Adopt `AddModuleLedgerClient` + `ModuleKit.Api` ref** — replace the `AddHttpClient(...).AddTypedClient(...)` with `services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("<name>", configuration);` (verify the HttpClient name string), add usings + `.Api.csproj` reference.

- [ ] **Step 5: Run the full Reconciliation suite**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Modules/Banking/Reconciliation
git commit -m "refactor(reconciliation): adopt ModuleKit base; relay via host middleware, drop per-endpoint catches"
```

---

### Task 7: Migrate FixedAssets (client + delete exception + remove read-endpoint try/catch)

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs`, `.../FixedAssetsEndpoints.cs`, `.../FixedAssetsServiceExtensions.cs`, `.../Accounting101.FixedAssets.Api.csproj`
- Delete: `Modules/FixedAssets/Accounting101.FixedAssets/LedgerClientException.cs`

**Interfaces:** Consumes Task 1.

- [ ] **Step 1: Swap the FA client to the base** — 2b subclass, key `"fixedassets"`, interface `IFixedAssetsLedgerClient`? No — the interface is `ILedgerClient` in namespace `Accounting101.FixedAssets` (verify: the FA `ILedgerClient` declares Post/Reverse/Void/GetEntriesBySourceRef/GetSubledger — all satisfied by the base). Subclass in namespace `Accounting101.FixedAssets.Api`.

- [ ] **Step 2: Delete `LedgerClientException.cs`.** FA has no `HttpLedgerClientTests`; check `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs` for any `LedgerClientException` reference and re-point with `using Accounting101.ModuleKit;` if present.

- [ ] **Step 3: Remove the read-endpoint try/catch (revert to pre-hardening bodies)**

In `FixedAssetsEndpoints.cs`, `GetAsset` and `ListAssets` each have a `try { … } catch (LedgerClientException ex) { … }` whose ONLY catch is `LedgerClientException` (added by the fold-on-read hardening). Remove the `try`/`catch` wrapper entirely — the middleware now owns the relay — reverting each to its plain body:

```csharp
private static async Task<IResult> GetAsset(
    Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
{
    Asset? asset = await service.GetAsync(clientId, assetId, cancellationToken);
    return asset is null ? Results.NotFound() : Results.Ok(new AssetView(asset));
}

private static async Task<IResult> ListAssets(
    Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
    FixedAssetsService service, CancellationToken cancellationToken)
{
    if (!TryOrder(order, out bool descending))
        return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

    PagedResponse<Asset> page = await service.GetByClientPagedAsync(
        clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

    return Results.Ok(new PagedResponse<AssetView>(
        page.Items.Select(a => new AssetView(a)).ToList(), page.Total, page.Skip, page.Limit));
}
```

- [ ] **Step 4: Adopt `AddModuleLedgerClient` + `ModuleKit.Api` ref** — in `FixedAssetsServiceExtensions.cs` replace the `AddHttpClient("FixedAssetsLedgerClient", …).AddTypedClient<…>()` with `services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("FixedAssetsLedgerClient", configuration);`, add usings + `.Api.csproj` reference.

- [ ] **Step 5: Run the full FA suite**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS (112/112) — `LedgerErrorRelayE2eTests` still returns relayed 422 on the misconfigured-chart read, now via the host middleware instead of the removed per-endpoint catch.

- [ ] **Step 6: Commit**

```bash
git add Modules/FixedAssets
git commit -m "refactor(fixedassets): adopt ModuleKit base; relay via host middleware, drop read-endpoint catches"
```

---

### Task 8: Migrate Inventory (client + delete exception + remove read-endpoint try/catch)

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs`, `.../InventoryEndpoints.cs`, `.../InventoryServiceExtensions.cs`, `.../Accounting101.Inventory.Api.csproj`
- Delete: `Modules/Inventory/Accounting101.Inventory/LedgerClientException.cs`

**Interfaces:** Consumes Task 1.

- [ ] **Step 1: Swap the Inventory client to the base** — 2b subclass, key `"inventory"`, interface `ILedgerClient` in namespace `Accounting101.Inventory.Api`. (Inventory's interface includes `GetEntriesBySourceRefsAsync` and `GetSubledgerAsync` — both satisfied by the base.)

- [ ] **Step 2: Delete `LedgerClientException.cs`.** Check `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs` for any reference; re-point with `using Accounting101.ModuleKit;` if present.

- [ ] **Step 3: Remove the read-endpoint try/catch (revert to pre-hardening bodies)**

In `InventoryEndpoints.cs`, `GetItem` and `ListItems` each have a `try { … } catch (LedgerClientException ex) { … }` with only the `LedgerClientException` arm (added by the fold-on-read hardening). Remove the wrapper entirely, reverting to:

```csharp
private static async Task<IResult> GetItem(
    Guid clientId, Guid itemId, InventoryService service, CancellationToken cancellationToken)
{
    Item? item = await service.GetAsync(clientId, itemId, cancellationToken);
    return item is null ? Results.NotFound() : Results.Ok(new ItemView(item));
}

private static async Task<IResult> ListItems(
    Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
    InventoryService service, CancellationToken cancellationToken)
{
    if (!TryOrder(order, out bool descending))
        return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

    PagedResponse<Item> page = await service.GetPagedAsync(
        clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

    return Results.Ok(new PagedResponse<ItemView>(
        page.Items.Select(i => new ItemView(i)).ToList(), page.Total, page.Skip, page.Limit));
}
```

(`ReactivateItem` is unchanged — it was never wrapped; it now relays via the middleware if its fold refuses, which is the desired write-path improvement.)

- [ ] **Step 4: Adopt `AddModuleLedgerClient` + `ModuleKit.Api` ref** — in `InventoryServiceExtensions.cs` replace the `AddHttpClient("InventoryLedgerClient", …).AddTypedClient<…>()` with `services.AddModuleLedgerClient<ILedgerClient, HttpLedgerClient>("InventoryLedgerClient", configuration);`, add usings + `.Api.csproj` reference.

- [ ] **Step 5: Run the full Inventory suite**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS (94/94) — `LedgerErrorRelayE2eTests` still returns relayed 422 via the middleware.

- [ ] **Step 6: Commit**

```bash
git add Modules/Inventory
git commit -m "refactor(inventory): adopt ModuleKit base; relay via host middleware, drop read-endpoint catches"
```

---

### Final verification (after all tasks)

- [ ] **Whole-solution build + test**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — whole solution green, including ModuleKit unit tests and every module's suite. (If a parallel-node OOM/MSB4166 flake occurs, the `-m:1` single-threaded run is the arbiter.)

- [ ] **Duplication is gone (grep proof)**

Run: `git ls-files | grep -E "LedgerClientException.cs|LedgerStatus.cs"` → expect only nothing under `Modules/` (the per-module copies deleted); `grep -rn "catch (LedgerClientException" Modules` → expect **zero** hits (all relays now via the host middleware).

## Success criteria (from spec)

- `LedgerClientException`, `LedgerTruth.ShowsVoided`, the relay helpers, and the client plumbing each have exactly one home; the per-endpoint catch arms drop to one host middleware.
- Every module's concrete client is a ctor-only 2b subclass; narrow per-module `ILedgerClient` interfaces unchanged.
- Two new assemblies, arrows point toward the engine only; engine untouched.
- No module write path manufactures a 500 from a clean engine 4xx (the Cash write-path relay E2E proves it); genuine engine 500s still relay as 500.
- Whole solution green — existing suites unchanged except namespace updates, plus the ModuleKit unit tests and the Cash write-path relay E2E.
