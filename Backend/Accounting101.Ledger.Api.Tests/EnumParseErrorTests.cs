using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Chart-of-accounts upsert: an invalid <c>Type</c> or <c>CashFlowActivity</c> wire value returns a
/// structured 422 <c>ValidationProblemDetails</c> naming the field, echoing the bad value, and listing
/// the valid values — instead of a raw <see cref="Enum.Parse{TEnum}(string)"/> exception message
/// ("Requested value 'aset' was not found."). Brings the account path to parity with the entry-post
/// path's structured <c>TryMapEntry</c> errors (see <see cref="StructuredValidationErrorsTests"/>).
/// </summary>
public sealed class EnumParseErrorTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task UpsertAccount_with_invalid_type_returns_structured_422_naming_valid_values()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/accounts/{Guid.NewGuid()}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "aset" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        ValidationProblemDetails? p = await resp.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(p);
        Assert.True(p!.Errors.ContainsKey("type"), $"Expected key 'type' in: {string.Join(", ", p.Errors.Keys)}");
        string msg = string.Join(" ", p.Errors["type"]);
        Assert.Contains("Asset", msg);   // lists the valid values
        Assert.Contains("aset", msg);    // echoes the bad value
    }

    [Fact]
    public async Task UpsertAccount_with_invalid_cash_flow_activity_returns_structured_422_naming_valid_values()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/accounts/{Guid.NewGuid()}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset", CashFlowActivity = "operatin" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        ValidationProblemDetails? p = await resp.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(p);
        Assert.True(p!.Errors.ContainsKey("cashFlowActivity"), $"Expected key 'cashFlowActivity' in: {string.Join(", ", p.Errors.Keys)}");
        string msg = string.Join(" ", p.Errors["cashFlowActivity"]);
        Assert.Contains("Operating", msg); // lists the valid values
        Assert.Contains("operatin", msg);  // echoes the bad value
    }

    /// <summary>Both fields bad at once — errors accumulate rather than stopping at the first bad field.</summary>
    [Fact]
    public async Task UpsertAccount_with_both_fields_invalid_collects_both_error_keys()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/accounts/{Guid.NewGuid()}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "aset", CashFlowActivity = "operatin" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        ValidationProblemDetails? p = await resp.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(p);
        Assert.True(p!.Errors.ContainsKey("type"), $"Missing 'type' in: {string.Join(", ", p.Errors.Keys)}");
        Assert.True(p.Errors.ContainsKey("cashFlowActivity"), $"Missing 'cashFlowActivity' in: {string.Join(", ", p.Errors.Keys)}");
    }

    /// <summary>Valid Type + CashFlowActivity still succeeds (unchanged behavior) — 200 OK, case-insensitive.</summary>
    [Fact]
    public async Task UpsertAccount_with_valid_type_and_cash_flow_activity_still_succeeds()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/accounts/{Guid.NewGuid()}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "asset", CashFlowActivity = "cash" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
