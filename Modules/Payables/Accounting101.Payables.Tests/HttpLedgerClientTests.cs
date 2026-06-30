using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Payables.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Http;

namespace Accounting101.Payables.Tests;

public sealed class HttpLedgerClientTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

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

    private static IHttpContextAccessor Context()
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Headers.Authorization = "DevToken abc";
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static HttpLedgerClient ClientFor(HttpResponseMessage response)
    {
        HttpClient http = new(new StubHandler(response)) { BaseAddress = new Uri("http://engine.local") };
        return new HttpLedgerClient(http, Context(), new ModuleCredential("test-key", "test-secret"));
    }

    [Fact]
    public async Task Post_throws_typed_ledger_exception_carrying_status_and_detail()
    {
        HttpLedgerClient client = ClientFor(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { title = "Conflict", status = 409, detail = "Period is closed through 2024-03-31." }),
        });
        PostEntryRequest entry = new(null, new DateOnly(2024, 3, 31), "BILL-1", null,
            [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.PostAsync(Guid.NewGuid(), entry));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("closed", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Void_throws_typed_ledger_exception_on_forbidden()
    {
        HttpLedgerClient client = ClientFor(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { title = "Forbidden", status = 403, detail = "Reverse requires the Approver role." }),
        });

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.VoidAsync(Guid.NewGuid(), Guid.NewGuid(), new VoidRequest("x")));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Approver", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_throws_with_field_level_text_on_validation_problem()
    {
        var body = new
        {
            title = "One or more validation errors occurred.", status = 422,
            detail = "One or more fields are invalid.",
            errors = new Dictionary<string, string[]> { ["lines[0].accountId"] = ["Account 2000 requires a Vendor dimension."] },
        };
        HttpLedgerClient client = ClientFor(new HttpResponseMessage((HttpStatusCode)422) { Content = JsonContent.Create(body) });
        PostEntryRequest entry = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        LedgerClientException ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => client.PostAsync(Guid.NewGuid(), entry));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("lines[0].accountId", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000", ex.Reason);
        Assert.DoesNotContain("One or more fields are invalid", ex.Reason);
    }

    [Fact]
    public async Task Validate_targets_the_validate_endpoint_and_attaches_the_module_credential()
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
        HttpLedgerClient client = new(http, Context(), new ModuleCredential("test-key", "test-secret"));

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 31), Reference: null, Memo: null,
            Lines: [new PostLineRequest(Guid.NewGuid(), "Debit", 100m)]);

        await client.ValidateAsync(clientId, entry);

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal($"http://engine.local/clients/{clientId}/entries/validate", handler.Last.RequestUri!.ToString());
        Assert.Equal("test-key", handler.Last.Headers.GetValues("X-Module-Key").Single());
    }
}
