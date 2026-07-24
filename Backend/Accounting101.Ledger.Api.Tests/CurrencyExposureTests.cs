using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CurrencyExposureTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public CurrencyExposureTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Capabilities_carry_the_clients_base_currency()
    {
        // Mint the http client first (boots the host), then register a EUR client + membership for that user.
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        HttpClient http = _fx.ClientFor(userId, "EU User", ("role", "Controller"));

        ControlStore control = _fx.Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Eurofirm", DatabaseName = "client_" + clientId.ToString("N"),
            BaseCurrency = "EUR",
        });
        await control.AddMembershipAsync(userId, clientId, LedgerRole.Controller);

        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{clientId}/me/capabilities"))!;
        Assert.Equal("EUR", caps.BaseCurrency);
    }

    [Fact]
    public async Task Currency_catalog_lists_real_currencies_and_excludes_X_codes()
    {
        HttpClient http = _fx.ClientFor(Guid.NewGuid(), "Anyone");
        List<CurrencyInfo> catalog = (await http.GetFromJsonAsync<List<CurrencyInfo>>("/currencies/catalog"))!;

        Assert.Contains(catalog, c => c is { Code: "EUR", Symbol: "€" });
        Assert.Contains(catalog, c => c.Code == "USD");
        Assert.DoesNotContain(catalog, c => c.Code is "XXX" or "XTS" or "XAU");
        Assert.All(catalog, c => Assert.False(string.IsNullOrWhiteSpace(c.Symbol)));
    }
}
