using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Api;

/// <summary>
/// The module's ledger client: a typed HttpClient onto the engine's own ledger endpoints. It forwards
/// the incoming request's Authorization header, so the engine authenticates and authorizes the same
/// user and applies its full endpoint policy (chart validation, SoD, RBAC). In the monolith this is a
/// loopback call; going out-of-process is only a base-address change.
/// <para>
/// For new-entry posts (<see cref="PostAsync"/>) and the pre-flight dry-run (<see cref="ValidateAsync"/>)
/// the module credential is attached as <c>X-Module-Key</c> / <c>X-Module-Secret</c> alongside the
/// forwarded bearer token. This lets the engine authorize the post under the module path and stamp
/// <c>ViaModule = "receivables"</c> on the resulting entry. The credential comes from DI
/// (<see cref="ModuleCredential"/>) so the secret is never hardcoded.
/// </para>
/// <para>
/// <see cref="ReverseAsync"/> and <see cref="VoidAsync"/> also attach the module credential
/// (<c>X-Module-Key</c>/<c>X-Module-Secret</c>). The engine's mutation endpoints require it to authorize a
/// correction of a module-owned entry: the correction must be driven by the owning module, not a raw user.
/// </para>
/// </summary>
public sealed class HttpLedgerClient(
    HttpClient http,
    IHttpContextAccessor context,
    [FromKeyedServices("receivables")] ModuleCredential credential) : ILedgerClient
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        // Attach the module credential so the engine authorizes via the module path and stamps ViaModule.
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PostEntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ApproveAsync(Guid clientId, Guid entryId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/approve");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/reverse");
        message.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        message.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/void");
        message.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        message.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/validate");
        // Attach the module credential so the engine's pre-flight dry-run authorizes via the module path.
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRef={sourceRef}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    /// <summary>
    /// Throw a typed <see cref="LedgerClientException"/> carrying the engine's status and reason on any
    /// non-success response, so a caller (and the module's endpoints) can relay the real cause instead of
    /// the bare <see cref="HttpRequestException"/> that <c>EnsureSuccessStatusCode</c> would throw.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

    /// <summary>
    /// Pull the best available reason from the response body.
    /// Priority: (1) <c>errors</c> map from ValidationProblemDetails (field-level messages flattened to
    /// <c>"field: msg; field: msg"</c>), (2) ProblemDetails <c>detail</c>, (3) raw body, (4) status phrase.
    /// The <c>errors</c> branch only fires when that property is a non-empty JSON object — plain
    /// ProblemDetails (409 freeze, etc.) that carry only <c>detail</c> are unaffected.
    /// </summary>
    private static string ReasonFrom(string body, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // ValidationProblemDetails: flatten the `errors` map when present and non-empty.
                    if (root.TryGetProperty("errors", out JsonElement errors)
                        && errors.ValueKind == JsonValueKind.Object)
                    {
                        StringBuilder sb = new();
                        foreach (JsonProperty prop in errors.EnumerateObject())
                        {
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(prop.Name).Append(": ");
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                sb.Append(string.Join(", ", prop.Value.EnumerateArray()
                                    .Select(m => m.GetString() ?? string.Empty)));
                            }
                            else
                            {
                                sb.Append(prop.Value.GetRawText().Trim('"'));
                            }
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }

                    // Plain ProblemDetails: use the `detail` field.
                    if (root.TryGetProperty("detail", out JsonElement detail)
                        && detail.ValueKind == JsonValueKind.String
                        && detail.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }
            catch (JsonException) { /* not JSON — relay the raw body */ }

            return body.Trim();
        }

        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
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
