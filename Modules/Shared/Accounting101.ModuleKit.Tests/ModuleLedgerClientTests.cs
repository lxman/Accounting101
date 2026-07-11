using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.ModuleKit;
using Accounting101.ModuleKit.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Http;

namespace Accounting101.ModuleKit.Tests;

public sealed class ModuleLedgerClientTests
{
    private sealed class TestLedgerClient(HttpClient http, IHttpContextAccessor context, ModuleCredential credential)
        : ModuleLedgerClient(http, context, credential);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public HttpResponseMessage Response = new(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                await request.Content.LoadIntoBufferAsync(cancellationToken);
            Last = request;
            return Response;
        }
    }

    private static IHttpContextAccessor ContextWith(string? authorization)
    {
        DefaultHttpContext ctx = new();
        if (authorization is not null)
            ctx.Request.Headers.Authorization = authorization;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    /// <summary>A dummy credential sufficient for unit-testing header construction.</summary>
    private static ModuleCredential DummyCredential() => new("test-key", "test-secret");

    [Fact]
    public async Task Post_forwards_the_authorization_header_and_targets_the_entries_endpoint()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new PostEntryResponse(Guid.NewGuid(), "Active", "PendingApproval")),
            },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        Guid clientId = Guid.NewGuid();
        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 31), Reference: "INV-00001", Memo: null,
            Lines: [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        await client.PostAsync(clientId, entry);

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal($"http://engine.local/clients/{clientId}/entries", handler.Last.RequestUri!.ToString());
        Assert.Equal("DevToken abc", handler.Last.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Post_throws_a_typed_ledger_exception_carrying_the_engine_status_and_reason()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                // The engine returns ProblemDetails; the reason lives in `detail`.
                Content = JsonContent.Create(new { title = "Conflict", status = 409, detail = "Period is closed through 2024-03-31." }),
            },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2024, 3, 31), Reference: "INV-1", Memo: null,
            Lines: [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.PostAsync(Guid.NewGuid(), entry));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("closed", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetEntriesBySourceRef_builds_the_source_ref_query()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<EntryResponse>()) },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();
        await client.GetEntriesBySourceRefAsync(clientId, sourceRef);

        Assert.Equal($"http://engine.local/clients/{clientId}/entries?sourceRef={sourceRef}", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Void_forwards_the_authorization_header_and_targets_the_void_endpoint()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EntryResponse(
                    Guid.NewGuid(), 0, default, "Standard", "Voided", "PendingApproval", 0,
                    null, null, null, null, [], null, null)),
            },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        Guid clientId = Guid.NewGuid();
        Guid entryId = Guid.NewGuid();
        await client.VoidAsync(clientId, entryId, new VoidRequest("test"));

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal($"http://engine.local/clients/{clientId}/entries/{entryId}/void", handler.Last.RequestUri!.ToString());
        Assert.Equal("DevToken abc", handler.Last.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Validate_returns_without_throwing_on_200_and_targets_the_validate_endpoint()
    {
        Guid clientId = Guid.NewGuid();
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { valid = true }),
            },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 31), Reference: null, Memo: null,
            Lines: [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        await client.ValidateAsync(clientId, entry);

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal($"http://engine.local/clients/{clientId}/entries/validate", handler.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Validate_throws_LedgerClientException_with_status_and_reason_on_409_problem_details()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { title = "Conflict", status = 409, detail = "Period is closed through 2024-03-31." }),
            },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2024, 3, 31), Reference: null, Memo: null,
            Lines: [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.ValidateAsync(Guid.NewGuid(), entry));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("closed", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the engine returns a ValidationProblemDetails 422 with an <c>errors</c> map (field-level
    /// messages), <see cref="ModuleLedgerClient"/> must surface the field text — not just the generic
    /// <c>detail</c> summary — so callers can relay the actual cause.
    /// </summary>
    [Fact]
    public async Task Validate_throws_LedgerClientException_with_field_level_text_on_422_validation_problem()
    {
        // This is the exact shape emitted by the engine after Task 1: a ValidationProblemDetails whose
        // `errors` map contains per-field messages.  The `detail` is only the bland summary.
        var body = new
        {
            title = "One or more validation errors occurred.",
            status = 422,
            detail = "One or more fields are invalid.",
            errors = new Dictionary<string, string[]>
            {
                ["lines[0].accountId"] = ["Account 1200 \"A/R\" requires a Customer dimension on the posting line."],
            },
        };

        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage((HttpStatusCode)422) { Content = JsonContent.Create(body) },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 31), Reference: null, Memo: null,
            Lines: [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.ValidateAsync(Guid.NewGuid(), entry));

        Assert.Equal(422, ex.StatusCode);
        // Must contain the field identifier and the account number, NOT just the generic summary.
        Assert.Contains("lines[0].accountId", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1200", ex.Reason);
        Assert.DoesNotContain("One or more fields are invalid", ex.Reason);
    }

    [Fact]
    public async Task GetAccounts_forwards_auth_and_targets_the_accounts_endpoint()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<AccountResponse>()),
            },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        Guid clientId = Guid.NewGuid();
        await client.GetAccountsAsync(clientId);

        Assert.Equal(HttpMethod.Get, handler.Last!.Method);
        Assert.Equal($"http://engine.local/clients/{clientId}/accounts", handler.Last.RequestUri!.ToString());
        Assert.Equal("DevToken abc", handler.Last.Headers.GetValues("Authorization").Single());
    }
}
