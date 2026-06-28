using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accounting101.Banking.Reconciliation;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>The module's POSTING ledger client. Forwards the caller's bearer; on PostAsync also attaches the
/// module credential (X-Module-Key/Secret) so the engine authorizes the module post and stamps
/// ViaModule="reconciliation". Reverse/Void/GetEntriesBySourceRef forward the bearer only. Non-success
/// responses throw a typed LedgerClientException so the module relays the engine's real status, not a 500.</summary>
public sealed class HttpLedgerClient(
    HttpClient http,
    IHttpContextAccessor context,
    [FromKeyedServices("reconciliation")] ModuleCredential credential) : ILedgerClient
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PostEntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/reverse");
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/void");
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRef={sourceRef}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

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
                    if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
                    {
                        StringBuilder sb = new();
                        foreach (JsonProperty prop in errors.EnumerateObject())
                        {
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(prop.Name).Append(": ");
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                                sb.Append(string.Join(", ", prop.Value.EnumerateArray().Select(m => m.GetString() ?? string.Empty)));
                            else
                                sb.Append(prop.Value.GetRawText().Trim('"'));
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }
                    if (root.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String && detail.GetString() is { Length: > 0 } text)
                        return text;
                }
            }
            catch (JsonException) { /* not JSON — relay the raw body */ }
            return body.Trim();
        }
        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
    }

    private HttpRequestMessage Forwarded(HttpMethod method, string uri)
    {
        HttpRequestMessage request = new(method, uri);
        string? authorization = context.HttpContext?.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }
}
