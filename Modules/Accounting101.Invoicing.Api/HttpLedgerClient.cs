using System.Net.Http.Json;
using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Api;

/// <summary>
/// The module's ledger client: a typed HttpClient onto the engine's own ledger endpoints. It forwards
/// the incoming request's Authorization header, so the engine authenticates and authorizes the same
/// user and applies its full endpoint policy (chart validation, SoD, RBAC). In the monolith this is a
/// loopback call; going out-of-process is only a base-address change.
/// </summary>
public sealed class HttpLedgerClient(HttpClient http, IHttpContextAccessor context) : ILedgerClient
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostEntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ApproveAsync(Guid clientId, Guid entryId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/approve");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/reverse");
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/void");
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRef={sourceRef}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    /// <summary>Build a request carrying the caller's bearer token, so the engine acts as that user.</summary>
    private HttpRequestMessage Forwarded(HttpMethod method, string uri)
    {
        HttpRequestMessage request = new(method, uri);
        string? authorization = context.HttpContext?.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }
}
