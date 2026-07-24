using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Static ISO 4217 currency catalog (code, symbol, name) — backend truth for the base-currency
/// picker and for resolving display symbols. Any authenticated user may read it.</summary>
public static class CurrencyCatalogEndpoints
{
    public static void MapCurrencyCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/currencies/catalog", () => Results.Ok(Iso4217Currencies.All)).RequireAuthorization();
    }
}
