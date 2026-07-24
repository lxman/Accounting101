using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CreateClientBaseCurrencyTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public CreateClientBaseCurrencyTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Create_with_EUR_persists_and_echoes()
    {
        HttpClient admin = _fx.AdminClient();
        HttpResponseMessage resp = await admin.PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "Eurofirm", BaseCurrency = "eur" }); // lower-case on purpose

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        ClientRegistrationResponse body = (await resp.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal("EUR", body.BaseCurrency); // normalized to upper-case
    }

    [Fact]
    public async Task Create_without_base_currency_defaults_to_USD()
    {
        HttpClient admin = _fx.AdminClient();
        HttpResponseMessage resp = await admin.PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "Domestic" });

        ClientRegistrationResponse body = (await resp.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal("USD", body.BaseCurrency);
    }

    [Theory]
    [InlineData("XXX")]
    [InlineData("XAU")]
    [InlineData("ZZZ")]
    public async Task Create_with_ineligible_code_is_422(string code)
    {
        HttpClient admin = _fx.AdminClient();
        HttpResponseMessage resp = await admin.PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "Bad", BaseCurrency = code });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Updating_another_field_preserves_base_currency()
    {
        // Register a EUR client directly, then run the SAME read-modify-write the fiscal-year setter uses,
        // and prove BaseCurrency survives (there is no setter for it; every update carries it forward).
        ControlStore control = _fx.Control();
        Guid id = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = id, Name = "Eurofirm", DatabaseName = "client_" + id.ToString("N"), BaseCurrency = "EUR",
        });

        ClientRegistration reg = (await control.GetClientAsync(id))!;
        reg.FiscalYearEndMonth = 6;                 // mutate a different field, read-modify-write
        await control.RegisterClientAsync(reg);

        ClientRegistration after = (await control.GetClientAsync(id))!;
        Assert.Equal("EUR", after.BaseCurrency);    // unchanged
        Assert.Equal(6, after.FiscalYearEndMonth);  // the intended change landed
    }
}
