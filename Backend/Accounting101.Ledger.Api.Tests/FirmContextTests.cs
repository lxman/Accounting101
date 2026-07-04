using System.Security.Claims;
using Accounting101.Ledger.Api.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FirmContextTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IHttpContextAccessor AccessorWith(params Claim[] claims)
    {
        DefaultHttpContext ctx = new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    [Fact]
    public void Uses_the_firm_claim_when_present()
    {
        Guid firmId = Guid.NewGuid();
        HttpContextFirmContext ctx = new(AccessorWith(new Claim(FirmClaims.FirmId, firmId.ToString())), EmptyConfig());
        Assert.Equal(firmId, ctx.FirmId);
    }

    [Fact]
    public void Falls_back_to_the_well_known_default_when_no_claim()
    {
        HttpContextFirmContext ctx = new(AccessorWith(), EmptyConfig());
        Assert.Equal(TenancyDefaults.DefaultFirmId, ctx.FirmId);
    }

    [Fact]
    public void Falls_back_to_the_configured_default_when_no_claim()
    {
        Guid configured = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tenancy:DefaultFirmId"] = configured.ToString(),
        }).Build();
        HttpContextFirmContext ctx = new(AccessorWith(), config);
        Assert.Equal(configured, ctx.FirmId);
    }
}
