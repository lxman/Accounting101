using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Api;

/// <summary>
/// The module's ledger client: a typed HttpClient onto the engine's own ledger endpoints. It forwards
/// the incoming request's Authorization header, so the engine authenticates and authorizes the same
/// user and applies its full endpoint policy (chart validation, SoD, RBAC). In the monolith this is a
/// loopback call; going out-of-process is only a base-address change.
/// <para>
/// For new-entry posts (<see cref="PostAsync"/>) the module credential is attached as
/// <c>X-Module-Key</c> / <c>X-Module-Secret</c> alongside the forwarded bearer token. This lets the
/// engine authorize the post under the module path and stamp <c>ViaModule = "fixedassets"</c> on the
/// resulting entry. The credential comes from DI (<see cref="ModuleCredential"/>) so the secret is
/// never hardcoded.
/// </para>
/// <para>
/// <see cref="ReverseAsync"/> and <see cref="VoidAsync"/> do NOT attach the credential. Those endpoints
/// authorize <c>Reverse</c>/<c>Void</c> permissions, not <c>Post</c>; the user carries those permissions
/// today and the engine does not require a module credential for them. Sending the credential anyway
/// would be harmless but misleading — the module is not the originator of those lifecycle transitions.
/// </para>
/// </summary>
public sealed class HttpLedgerClient(
    HttpClient http,
    IHttpContextAccessor context,
    [FromKeyedServices("fixedassets")] ModuleCredential credential) : ILedgerClient
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        // Attach the module credential so the engine authorizes via the module path and stamps ViaModule.
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostEntryResponse>(cancellationToken))!;
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
