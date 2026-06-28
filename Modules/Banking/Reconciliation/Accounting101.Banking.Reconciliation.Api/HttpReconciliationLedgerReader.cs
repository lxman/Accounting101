using System.Net.Http.Json;
using Accounting101.Banking.Reconciliation;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>Read-only loopback onto the engine: forwards the caller's bearer token to the entries-by-account
/// and trial-balance reads. No module credential — slice 1 never posts.</summary>
public sealed class HttpReconciliationLedgerReader(HttpClient http, IHttpContextAccessor context) : IReconciliationLedgerReader
{
    public async Task<IReadOnlyList<EntryResponse>> GetEntriesTouchingAccountAsync(Guid clientId, Guid accountId, CancellationToken ct = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?account={accountId}");
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(ct))!;
    }

    public async Task<decimal> GetCashBalanceAsync(Guid clientId, Guid accountId, DateOnly asOf, CancellationToken ct = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/trial-balance?asOf={asOf:yyyy-MM-dd}");
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        TrialBalanceResponse tb = (await response.Content.ReadFromJsonAsync<TrialBalanceResponse>(ct))!;
        return tb.Accounts.FirstOrDefault(a => a.AccountId == accountId)?.Balance ?? 0m;
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
