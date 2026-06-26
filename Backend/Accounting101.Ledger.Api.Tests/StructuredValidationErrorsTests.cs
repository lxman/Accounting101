using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Field-level validation: <c>POST /clients/{clientId}/entries</c> (and its dry-run
/// <c>/entries/validate</c>) return <c>ValidationProblemDetails</c> with a structured
/// <c>errors</c> map keyed by field path (e.g. <c>lines[0].direction</c>, <c>type</c>,
/// <c>balance</c>, <c>lines[{i}].accountId</c>) instead of a flat <c>detail</c> string.
/// </summary>
public sealed class StructuredValidationErrorsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ---- helpers -------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> PostEntryAsync(HttpClient http, Guid clientId, PostEntryRequest request) =>
        http.PostAsJsonAsync($"/clients/{clientId}/entries", request);

    private static Task<HttpResponseMessage> ValidateEntryAsync(HttpClient http, Guid clientId, PostEntryRequest request) =>
        http.PostAsJsonAsync($"/clients/{clientId}/entries/validate", request);

    /// <summary>Parse the <c>errors</c> map from a ValidationProblemDetails response body.</summary>
    private static async Task<Dictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage resp)
    {
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("errors", out JsonElement errorsEl))
            return errors;
        foreach (JsonProperty prop in errorsEl.EnumerateObject())
        {
            List<string> messages = [];
            foreach (JsonElement msg in prop.Value.EnumerateArray())
                messages.Add(msg.GetString() ?? "");
            errors[prop.Name] = [.. messages];
        }
        return errors;
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient http, Guid clientId, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    // ---- direction errors ----------------------------------------------------------------------

    /// <summary>An unrecognised direction ("dr") returns 422 with lines[0].direction keyed error
    /// containing both "Debit" and "Credit" as acceptable values.</summary>
    [Fact]
    public async Task Bad_direction_returns_422_with_lines_direction_key()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(a, "dr", 100m),
                new PostLineRequest(b, "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines[0].direction"), $"Expected key 'lines[0].direction' in: {string.Join(", ", errors.Keys)}");
        string msg = string.Join(" ", errors["lines[0].direction"]);
        Assert.Contains("Debit", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Credit", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lines[1].direction", errors.Keys); // line 1 is valid
    }

    /// <summary>Two bad directions on two lines — errors accumulate (no first-throw stop).</summary>
    [Fact]
    public async Task Two_bad_directions_returns_422_with_both_line_keys_collected()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(a, "dr", 100m),
                new PostLineRequest(b, "cr", 100m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines[0].direction"), $"Missing lines[0].direction in: {string.Join(", ", errors.Keys)}");
        Assert.True(errors.ContainsKey("lines[1].direction"), $"Missing lines[1].direction in: {string.Join(", ", errors.Keys)}");
    }

    /// <summary>A null/empty direction returns the required-field message keyed on lines[{i}].direction.</summary>
    [Fact]
    public async Task Missing_direction_returns_422_with_required_field_message()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        // Send empty string as direction — the null check covers empty as well
        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(a, "", 100m),
                new PostLineRequest(b, "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines[0].direction"), $"Missing lines[0].direction in: {string.Join(", ", errors.Keys)}");
        string msg = string.Join(" ", errors["lines[0].direction"]);
        Assert.Contains("required", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ---- type error ----------------------------------------------------------------------------

    /// <summary>An unpostable type ("Closing") returns 422 with a "type" keyed error.</summary>
    [Fact]
    public async Task Bad_type_returns_422_with_type_key()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(a, "Debit", 100m),
                new PostLineRequest(b, "Credit", 100m),
            ],
            Type: "Closing"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("type"), $"Expected key 'type' in: {string.Join(", ", errors.Keys)}");
        string msg = string.Join(" ", errors["type"]);
        Assert.Contains("Standard", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Adjusting", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ---- balance error -------------------------------------------------------------------------

    /// <summary>An unbalanced entry returns 422 with a "balance" keyed error mentioning the imbalance.</summary>
    [Fact]
    public async Task Unbalanced_entry_returns_422_with_balance_key()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(a, "Debit", 100m),
                new PostLineRequest(b, "Credit", 99m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("balance"), $"Expected key 'balance' in: {string.Join(", ", errors.Keys)}");
        // Message should mention the phrase "debits minus credits" and the numeric imbalance
        string msg = string.Join(" ", errors["balance"]);
        Assert.Contains("debits minus credits", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", msg); // the imbalance amount (decimal formats as "1")
    }

    // ---- too-few-lines errors (C1 fix) ---------------------------------------------------------

    /// <summary>Posting a single line returns 422 with errors["lines"] containing "at least two lines".</summary>
    [Fact]
    public async Task One_line_entry_returns_422_with_lines_key()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines"), $"Expected key 'lines' in: {string.Join(", ", errors.Keys)}");
        string msg = string.Join(" ", errors["lines"]);
        Assert.Contains("at least two lines", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Posting zero lines returns 422 with errors["lines"] containing "at least two lines".</summary>
    [Fact]
    public async Task Zero_line_entry_returns_422_with_lines_key()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null, []));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines"), $"Expected key 'lines' in: {string.Join(", ", errors.Keys)}");
        string msg = string.Join(" ", errors["lines"]);
        Assert.Contains("at least two lines", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ---- chart violations (per-line keyed on lines[{i}].accountId) ----------------------------

    /// <summary>A required dimension missing on a control account returns 422 keyed on lines[{i}].accountId.</summary>
    [Fact]
    public async Task Missing_required_dimension_returns_422_with_lines_accountId_key()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid receivable = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(receivable, "Debit", 100m), // missing Customer dimension
                new PostLineRequest(revenue, "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines[0].accountId"), $"Expected key 'lines[0].accountId' in: {string.Join(", ", errors.Keys)}");
        string msg = string.Join(" ", errors["lines[0].accountId"]);
        Assert.Contains("Customer", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two chart violations on two different lines — both line keys appear in errors map.</summary>
    [Fact]
    public async Task Two_chart_violations_returns_422_with_both_lines_accountId_keys()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid ap = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "2000", Name = "Accounts Payable", Type = "Liability", RequiredDimension = "Vendor" });

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(ar, "Debit", 100m),  // missing Customer
                new PostLineRequest(ap, "Credit", 100m), // missing Vendor
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines[0].accountId"), $"Missing lines[0].accountId in: {string.Join(", ", errors.Keys)}");
        Assert.True(errors.ContainsKey("lines[1].accountId"), $"Missing lines[1].accountId in: {string.Join(", ", errors.Keys)}");
    }

    // ---- unchanged behaviors -------------------------------------------------------------------

    /// <summary>A valid entry posts successfully (201).</summary>
    [Fact]
    public async Task Valid_entry_returns_201()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    /// <summary>Validate endpoint on a valid entry returns 200 {valid:true}.</summary>
    [Fact]
    public async Task Valid_entry_validate_returns_200_valid_true()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await ValidateEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        EntryValidationResponse? body = await resp.Content.ReadFromJsonAsync<EntryValidationResponse>();
        Assert.True(body?.Valid);
    }

    /// <summary>Closed period still returns 409 (the freeze path is unchanged).</summary>
    [Fact]
    public async Task Closed_period_returns_409_unchanged()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();

        // Post + approve + close
        HttpResponseMessage posted = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(debit, "Debit", 100m),
                new PostLineRequest(credit, "Credit", 100m),
            ]));
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2026, 3, 31)))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 15), null, null,
            [
                new PostLineRequest(debit, "Debit", 100m),
                new PostLineRequest(credit, "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    /// <summary>Same id, different content idempotency conflict returns 422 (plain detail, no errors map).</summary>
    [Fact]
    public async Task Idempotency_conflict_returns_422_plain_detail_no_errors_map()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        Guid id = Guid.NewGuid();

        (await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(id, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(debit, "Debit", 100m),
                new PostLineRequest(credit, "Credit", 100m),
            ]))).EnsureSuccessStatusCode();

        HttpResponseMessage clash = await PostEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(id, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(debit, "Debit", 200m),
                new PostLineRequest(credit, "Credit", 200m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
        // This 422 is a plain ProblemDetails, NOT a ValidationProblemDetails — no "errors" key.
        string json = await clash.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        // The idempotency conflict should NOT have a structured errors map
        Assert.False(doc.RootElement.TryGetProperty("errors", out _),
            "Idempotency conflict 422 should be a plain ProblemDetails, not a ValidationProblemDetails with an errors map");
    }

    // ---- validate endpoint parity --------------------------------------------------------------

    /// <summary>validate endpoint on an unbalanced entry also returns 422 with balance key.</summary>
    [Fact]
    public async Task Validate_unbalanced_returns_422_with_balance_key()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await ValidateEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 50m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("balance"), $"Expected key 'balance' in: {string.Join(", ", errors.Keys)}");
    }

    /// <summary>validate endpoint on a bad direction returns 422 with lines direction key.</summary>
    [Fact]
    public async Task Validate_bad_direction_returns_422_with_lines_direction_key()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage resp = await ValidateEntryAsync(c.Http, c.ClientId,
            new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [
                new PostLineRequest(Guid.NewGuid(), "INVALID", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Dictionary<string, string[]> errors = await ReadErrorsAsync(resp);
        Assert.True(errors.ContainsKey("lines[0].direction"), $"Expected key 'lines[0].direction' in: {string.Join(", ", errors.Keys)}");
    }
}
