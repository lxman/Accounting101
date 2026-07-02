using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Verifies the module-authorized posting path introduced in Task 3:
/// <list type="bullet">
///   <item>A user whose role lacks <see cref="Permission.Post"/> can post when a valid module
///     credential is supplied — the module's authorization substitutes for the user's permission.</item>
///   <item>The same user without module headers gets 403 (raw path enforces the role).</item>
///   <item>A raw Clerk post (no headers) still gets 201 and <c>ViaModule</c> is null.</item>
///   <item>A disabled module's credentials are rejected with 403 (authorization, not authentication).</item>
///   <item>Even with module headers, <c>CreatedBy</c> is the token bearer, not the module.</item>
/// </list>
/// </summary>
public sealed class ModulePostingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const string ModuleKey = "payables";
    private const string ModuleSecret = "test-secret-42";
    private const string DisabledKey = "disabled-mod";
    private const string DisabledSecret = "disabled-secret-99";

    // ---- helpers -------------------------------------------------------------------------------

    private static PostEntryRequest BalancedEntry() =>
        new(
            Id: null,
            EffectiveDate: new DateOnly(2026, 6, 26),
            Reference: "MOD-POST-TEST",
            Memo: "module posting test",
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ]);

    /// <summary>
    /// Sends a POST /entries request using an <see cref="HttpRequestMessage"/> so we can attach
    /// module headers per-request without mutating the client's DefaultRequestHeaders.
    /// </summary>
    private static async Task<HttpResponseMessage> PostWithModuleAsync(
        HttpClient http, Guid clientId, PostEntryRequest body,
        string moduleKey, string moduleSecret)
    {
        HttpRequestMessage req = new(HttpMethod.Post, $"/clients/{clientId}/entries");
        req.Headers.Add("X-Module-Key", moduleKey);
        req.Headers.Add("X-Module-Secret", moduleSecret);
        req.Content = JsonContent.Create(body);
        return await http.SendAsync(req);
    }

    // ---- tests ---------------------------------------------------------------------------------

    /// <summary>
    /// A Clerk holds ap.write but not gl.post. When a registered + enabled module credential is
    /// supplied, the module authorization substitutes for the raw gl.post permission. The response
    /// must be 201 and the entry must carry ViaModule = "payables".
    /// </summary>
    [Fact]
    public async Task Module_credential_allows_clerk_to_post_and_stamps_ViaModule()
    {
        // Seed a client with a Clerk owner (to set up the client registration); add a module Clerk member.
        SeededClient c = await fixture.SeedClientAsync("ModulePostTest");

        Guid clerkId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(clerkId, c.ClientId, LedgerRole.Clerk);
        HttpClient clerkHttp = fixture.ClientFor(clerkId, "Clerk");

        // Register the module in the control DB.
        await fixture.Control().RegisterModuleAsync(new ModuleRegistration
        {
            Key = ModuleKey,
            Name = "Payables",
            Enabled = true,
            Secret = ModuleSecret,
        });

        PostEntryRequest body = BalancedEntry();
        HttpResponseMessage resp = await PostWithModuleAsync(clerkHttp, c.ClientId, body, ModuleKey, ModuleSecret);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        PostEntryResponse created = (await resp.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        EntryResponse? entry = await clerkHttp.GetFromJsonAsync<EntryResponse>(
            $"/clients/{c.ClientId}/entries/{created.Id}");

        Assert.NotNull(entry);
        Assert.Equal(ModuleKey, entry!.ViaModule);
    }

    /// <summary>
    /// Same Clerk, no module headers → 403. A Clerk holds ap.write but not gl.post, so the raw path
    /// still refuses it — this proves the module path was the authorization that allowed the post in
    /// the previous test, not some other loophole.
    /// </summary>
    [Fact]
    public async Task Clerk_without_module_headers_gets_403()
    {
        SeededClient c = await fixture.SeedClientAsync("ModulePostTest403");

        Guid clerkId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(clerkId, c.ClientId, LedgerRole.Clerk);
        HttpClient clerkHttp = fixture.ClientFor(clerkId, "Clerk");

        HttpResponseMessage resp = await clerkHttp.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries", BalancedEntry());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    /// <summary>
    /// A Clerk posting without any module headers succeeds (201) and ViaModule is null — the raw
    /// path is completely unchanged.
    /// </summary>
    [Fact]
    public async Task Raw_clerk_post_succeeds_and_ViaModule_is_null()
    {
        SeededClient c = await fixture.SeedClientAsync("RawClerkPost");

        HttpResponseMessage resp = await c.Http.PostAsJsonAsync(
            $"/clients/{c.ClientId}/entries", BalancedEntry());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        PostEntryResponse created = (await resp.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        EntryResponse? entry = await c.Http.GetFromJsonAsync<EntryResponse>(
            $"/clients/{c.ClientId}/entries/{created.Id}");

        Assert.NotNull(entry);
        Assert.Null(entry!.ViaModule);
    }

    /// <summary>
    /// A disabled module's credentials authenticate successfully but are refused by ModuleAccess
    /// (authorization). The endpoint must return 403, not 201.
    /// </summary>
    [Fact]
    public async Task Disabled_module_credentials_are_refused_with_403()
    {
        SeededClient c = await fixture.SeedClientAsync("DisabledModuleTest");

        Guid approverId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverId, c.ClientId, LedgerRole.Approver);
        HttpClient approverHttp = fixture.ClientFor(approverId, "Approver");

        // Register the module as disabled.
        await fixture.Control().RegisterModuleAsync(new ModuleRegistration
        {
            Key = DisabledKey,
            Name = "Disabled Module",
            Enabled = false,
            Secret = DisabledSecret,
        });

        HttpResponseMessage resp = await PostWithModuleAsync(
            approverHttp, c.ClientId, BalancedEntry(), DisabledKey, DisabledSecret);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    /// <summary>
    /// Even when a module credential is supplied, the entry's CreatedBy must be the token bearer
    /// (the user), not the module. The module is the authorizing party, not the actor.
    /// </summary>
    [Fact]
    public async Task Actor_is_always_the_token_bearer_even_with_module_headers()
    {
        SeededClient c = await fixture.SeedClientAsync("ActorTest");

        Guid clerkId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(clerkId, c.ClientId, LedgerRole.Clerk);
        HttpClient clerkHttp = fixture.ClientFor(clerkId, "Clerk");

        // Use the same "payables" module (may already exist from a prior test — RegisterModuleAsync is upsert).
        await fixture.Control().RegisterModuleAsync(new ModuleRegistration
        {
            Key = ModuleKey,
            Name = "Payables",
            Enabled = true,
            Secret = ModuleSecret,
        });

        HttpResponseMessage resp = await PostWithModuleAsync(
            clerkHttp, c.ClientId, BalancedEntry(), ModuleKey, ModuleSecret);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        PostEntryResponse created = (await resp.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        // Pull the audit trail to read CreatedBy.
        List<AuditRecordResponse>? audit = await clerkHttp.GetFromJsonAsync<List<AuditRecordResponse>>(
            $"/clients/{c.ClientId}/audit/{created.Id}");

        Assert.NotNull(audit);
        AuditRecordResponse first = audit!.First();
        Assert.Equal(clerkId, first.Actor.UserId);
    }

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
}
