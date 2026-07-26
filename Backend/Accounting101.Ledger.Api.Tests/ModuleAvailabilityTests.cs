using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Availability = installed ∩ entitled. Every surface that answers "can this client use module X?"
/// must answer with the intersection; a client entitled to a module this deployment never composed
/// would otherwise get nav links to routes nobody registered.
/// </summary>
public sealed class ModuleAvailabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Capabilities_lists_an_entitled_module_that_is_installed()
    {
        SeededClient c = await fixture.SeedClientAsync("AvailInstalled", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash", "reconciliation"]);

        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash", "reconciliation"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Equal(["cash", "reconciliation"], caps.EnabledModules);
    }

    [Fact]
    public async Task Capabilities_omits_an_entitled_module_that_is_not_installed()
    {
        SeededClient c = await fixture.SeedClientAsync("AvailAbsent", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash", "reconciliation"]);

        // This deployment composed "cash" only — "reconciliation" was sold but is not here.
        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Equal(["cash"], caps.EnabledModules);
    }

    [Fact]
    public async Task Capabilities_omits_an_installed_module_the_client_is_not_entitled_to()
    {
        // The default-closed property, pinned: installation alone never grants availability.
        SeededClient c = await fixture.SeedClientAsync("AvailNotSold", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash"]);

        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash", "reconciliation"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Equal(["cash"], caps.EnabledModules);
    }

    [Fact]
    public async Task Entitlement_survives_a_module_being_uninstalled()
    {
        // The intersection is a VIEW, not a migration: a redeploy must never silently delete what a
        // customer bought. Reading availability through a host lacking the module must not rewrite the
        // stored entitlement — re-installing has to restore the feature.
        SeededClient c = await fixture.SeedClientAsync("AvailSurvives", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash", "reconciliation"]);

        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;
        Assert.DoesNotContain("reconciliation", caps.EnabledModules);

        ClientRegistration? stored = await fixture.Control().GetClientAsync(c.ClientId);
        Assert.NotNull(stored);
        Assert.Contains("reconciliation", stored!.EnabledModules);
    }

    [Fact]
    public async Task Posting_account_slots_are_offered_for_an_installed_entitled_module()
    {
        SeededClient c = await fixture.SeedClientAsync("SlotsInstalled");
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash"]);
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(
            userId, c.ClientId, [], [Capabilities.AdminPostingAccounts, Capabilities.GlRead]);

        HttpClient http = fixture.ClientWithInstalledModules(userId, ["cash"]);
        PostingAccountsResponse response = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;

        Assert.Equal("cash", Assert.Single(response.Slots).ModuleKey);
    }

    [Fact]
    public async Task Posting_account_slots_are_not_offered_for_an_uninstalled_module()
    {
        // Offering a config slot for a module this deployment never composed is the same lie the
        // capabilities payload used to tell, in a different place.
        SeededClient c = await fixture.SeedClientAsync("SlotsAbsent");
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash"]);
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(
            userId, c.ClientId, [], [Capabilities.AdminPostingAccounts, Capabilities.GlRead]);

        HttpClient http = fixture.ClientWithInstalledModules(userId, ["reconciliation"]);
        PostingAccountsResponse response = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;

        Assert.Empty(response.Slots);
    }
}
