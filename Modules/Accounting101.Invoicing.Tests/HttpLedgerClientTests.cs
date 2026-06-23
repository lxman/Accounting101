using System.Net;
using System.Net.Http.Json;
using Accounting101.Invoicing.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Http;

namespace Accounting101.Invoicing.Tests;

public sealed class HttpLedgerClientTests
{
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
        HttpLedgerClient client = new(http, ContextWith("DevToken abc"));

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
    public async Task GetEntriesBySourceRef_builds_the_source_ref_query()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<EntryResponse>()) },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        HttpLedgerClient client = new(http, ContextWith("DevToken abc"));

        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();
        await client.GetEntriesBySourceRefAsync(clientId, sourceRef);

        Assert.Equal($"http://engine.local/clients/{clientId}/entries?sourceRef={sourceRef}", handler.Last!.RequestUri!.ToString());
    }
}
