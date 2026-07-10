using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Http;

namespace Accounting101.ModuleKit.Api;

/// <summary>
/// The shared base for every module's ledger client: a typed HttpClient onto the engine's ledger
/// endpoints. It forwards the caller's Authorization header (so the engine authenticates the same user
/// and applies its full policy), attaches the owning module's credential on write operations (so the
/// engine authorizes under the module path and stamps <c>ViaModule</c>), and relays any non-2xx as a
/// typed <see cref="LedgerClientException"/>. Each module derives a thin concrete client that supplies
/// its keyed <see cref="ModuleCredential"/> and implements its own narrow ledger-client interface; the
/// public members here satisfy that interface.
/// </summary>
public abstract class ModuleLedgerClient(HttpClient http, IHttpContextAccessor context, ModuleCredential credential)
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        WithModuleCredential(request);
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
        WithModuleCredential(message);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/void");
        WithModuleCredential(message);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/validate");
        WithModuleCredential(request);
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

    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(
        Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        if (sourceRefs.Count == 0) return [];
        string csv = string.Join(',', sourceRefs);
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRefs={csv}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        string url = $"clients/{clientId}/subledger?account={account}&dimension={Uri.EscapeDataString(dimension)}";
        if (asOf is { } d) url += $"&asOf={d:yyyy-MM-dd}";
        if (includePending) url += "&includePending=true";
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, url);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        SubledgerResponse body = (await response.Content.ReadFromJsonAsync<SubledgerResponse>(cancellationToken))!;
        return body.Lines;
    }

    private void WithModuleCredential(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
    }

    private HttpRequestMessage Forwarded(HttpMethod method, string uri)
    {
        HttpRequestMessage request = new(method, uri);
        string? authorization = context.HttpContext?.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

    /// <summary>
    /// Pull the best available reason from the response body.
    /// Priority: (1) <c>errors</c> map from ValidationProblemDetails (flattened to <c>"field: msg; …"</c>),
    /// (2) ProblemDetails <c>detail</c>, (3) raw body, (4) status phrase.
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
}
