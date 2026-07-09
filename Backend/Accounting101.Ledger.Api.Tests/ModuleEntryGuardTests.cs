using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The module-owned entry guard: a void/reverse/revise of an entry stamped with a ViaModule must be
/// driven by the owning module (its credential), not a raw journal call. Manual entries are unchanged.</summary>
public sealed class ModuleEntryGuardTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const string ModuleKey = "payables";
    private const string ModuleSecret = "guard-test-secret";

    private static PostEntryRequest Balanced() =>
        new(null, new DateOnly(2026, 6, 26), "GUARD", "guard test",
            [new PostLineRequest(Guid.NewGuid(), "Debit", 100m), new PostLineRequest(Guid.NewGuid(), "Credit", 100m)]);

    private static async Task<HttpResponseMessage> PostWithModuleAsync(
        HttpClient http, Guid clientId, PostEntryRequest body, string key, string secret)
    {
        HttpRequestMessage req = new(HttpMethod.Post, $"/clients/{clientId}/entries");
        req.Headers.Add("X-Module-Key", key);
        req.Headers.Add("X-Module-Secret", secret);
        req.Content = JsonContent.Create(body);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> MutateWithModuleAsync(
        HttpClient http, Guid clientId, string path, object body, string key, string secret)
    {
        HttpRequestMessage req = new(HttpMethod.Post, $"/clients/{clientId}/entries/{path}");
        req.Headers.Add("X-Module-Key", key);
        req.Headers.Add("X-Module-Secret", secret);
        req.Content = JsonContent.Create(body);
        return await http.SendAsync(req);
    }

    /// <summary>Register the guard-test module (upsert) and add a Clerk (holds ap.write) to drive it.</summary>
    private async Task<(SeededClient Client, HttpClient Clerk)> SeedWithModuleClerkAsync(string name)
    {
        SeededClient c = await fixture.SeedClientAsync(name);
        await fixture.Control().RegisterModuleAsync(new ModuleRegistration
        {
            Key = ModuleKey, Name = "Payables", Enabled = true, Secret = ModuleSecret,
        });
        Guid clerkId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(clerkId, c.ClientId, LedgerRole.Clerk);
        return (c, fixture.ClientFor(clerkId, "Clerk"));
    }

    private static async Task<Guid> PostModuleEntryAsync((SeededClient Client, HttpClient Clerk) seed)
    {
        HttpResponseMessage posted = await PostWithModuleAsync(seed.Clerk, seed.Client.ClientId, Balanced(), ModuleKey, ModuleSecret);
        posted.EnsureSuccessStatusCode();
        return (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!.Id;
    }

    // ---- manual entries: unchanged ----

    [Fact]
    public async Task Raw_void_of_a_manual_entry_still_succeeds()
    {
        SeededClient c = await fixture.SeedClientAsync("GuardManualVoid");
        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", Balanced());
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        HttpResponseMessage voided = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{created.Id}/void", new VoidRequest("manual void"));
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
    }

    [Fact]
    public async Task Raw_void_of_a_manual_entry_without_permission_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync("GuardManualVoidNoPerm");
        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", Balanced());
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        HttpClient auditor = await fixture.AddMemberAsync(c.ClientId, LedgerRole.Auditor, "Auditor"); // no gl.void
        HttpResponseMessage voided = await auditor.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{created.Id}/void", new VoidRequest("nope"));
        Assert.Equal(HttpStatusCode.Forbidden, voided.StatusCode);
    }

    // ---- module-owned entries: guarded ----

    [Fact]
    public async Task Raw_void_of_a_module_owned_entry_is_refused()
    {
        var seed = await SeedWithModuleClerkAsync("GuardModuleVoidRefused");
        Guid entryId = await PostModuleEntryAsync(seed);

        // The Controller (seed.Client.Http) holds gl.void, but the entry is module-owned and the request
        // carries no module credential → refused.
        HttpResponseMessage raw = await seed.Client.Http.PostAsJsonAsync(
            $"/clients/{seed.Client.ClientId}/entries/{entryId}/void", new VoidRequest("raw void"));
        Assert.Equal(HttpStatusCode.Conflict, raw.StatusCode);
        Assert.Contains("through that module", await raw.Content.ReadAsStringAsync());

        EntryResponse after = (await seed.Client.Http.GetFromJsonAsync<EntryResponse>(
            $"/clients/{seed.Client.ClientId}/entries/{entryId}"))!;
        Assert.Equal("Active", after.Status);
    }

    [Fact]
    public async Task Module_credentialed_void_of_a_module_owned_entry_is_allowed()
    {
        var seed = await SeedWithModuleClerkAsync("GuardModuleVoidAllowed");
        Guid entryId = await PostModuleEntryAsync(seed);

        // The module drives its own void: credential present + matching ViaModule, and the Clerk holds ap.write.
        HttpResponseMessage viaModule = await MutateWithModuleAsync(
            seed.Clerk, seed.Client.ClientId, $"{entryId}/void", new VoidRequest("through module"), ModuleKey, ModuleSecret);
        Assert.Equal(HttpStatusCode.OK, viaModule.StatusCode);
    }

    [Fact]
    public async Task Raw_reverse_of_a_module_owned_entry_is_refused()
    {
        var seed = await SeedWithModuleClerkAsync("GuardModuleReverseRefused");
        Guid entryId = await PostModuleEntryAsync(seed);
        (await seed.Client.Http.PostAsync($"/clients/{seed.Client.ClientId}/entries/{entryId}/approve", null)).EnsureSuccessStatusCode();

        HttpResponseMessage raw = await seed.Client.Http.PostAsJsonAsync(
            $"/clients/{seed.Client.ClientId}/entries/{entryId}/reverse", new ReverseRequest(new DateOnly(2026, 7, 1), "raw reverse"));
        Assert.Equal(HttpStatusCode.Conflict, raw.StatusCode);
        Assert.Contains("through that module", await raw.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Module_credentialed_reverse_of_a_module_owned_entry_is_allowed()
    {
        var seed = await SeedWithModuleClerkAsync("GuardModuleReverseAllowed");
        Guid entryId = await PostModuleEntryAsync(seed);
        (await seed.Client.Http.PostAsync($"/clients/{seed.Client.ClientId}/entries/{entryId}/approve", null)).EnsureSuccessStatusCode();

        HttpResponseMessage viaModule = await MutateWithModuleAsync(
            seed.Clerk, seed.Client.ClientId, $"{entryId}/reverse",
            new ReverseRequest(new DateOnly(2026, 7, 1), "through module"), ModuleKey, ModuleSecret);
        Assert.Equal(HttpStatusCode.Created, viaModule.StatusCode);
    }

    [Fact]
    public async Task Raw_reverse_of_a_manual_posted_entry_still_succeeds()
    {
        SeededClient c = await fixture.SeedClientAsync("GuardManualReverse");
        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", Balanced());
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();

        HttpResponseMessage reversed = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{created.Id}/reverse", new ReverseRequest(new DateOnly(2026, 7, 1), "manual reverse"));
        Assert.Equal(HttpStatusCode.Created, reversed.StatusCode);
    }

    [Fact]
    public async Task Raw_revise_of_a_module_owned_entry_is_refused()
    {
        var seed = await SeedWithModuleClerkAsync("GuardModuleReviseRefused");
        Guid entryId = await PostModuleEntryAsync(seed);

        HttpResponseMessage raw = await seed.Client.Http.PostAsJsonAsync(
            $"/clients/{seed.Client.ClientId}/entries/{entryId}/revise",
            new ReviseRequest(null, new DateOnly(2026, 6, 26), "GUARD-REV", "revised", "correction",
                [new PostLineRequest(Guid.NewGuid(), "Debit", 100m), new PostLineRequest(Guid.NewGuid(), "Credit", 100m)]));
        Assert.Equal(HttpStatusCode.Conflict, raw.StatusCode);
        Assert.Contains("through that module", await raw.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Raw_revise_of_a_manual_entry_still_succeeds()
    {
        SeededClient c = await fixture.SeedClientAsync("GuardManualRevise");
        HttpResponseMessage posted = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", Balanced());
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();

        HttpResponseMessage revised = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries/{created.Id}/revise",
            new ReviseRequest(null, new DateOnly(2026, 6, 26), "GUARD-REV", "revised", "correction",
                [new PostLineRequest(Guid.NewGuid(), "Debit", 100m), new PostLineRequest(Guid.NewGuid(), "Credit", 100m)]));
        Assert.Equal(HttpStatusCode.Created, revised.StatusCode);
    }
}
