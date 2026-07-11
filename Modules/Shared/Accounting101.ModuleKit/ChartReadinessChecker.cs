using Accounting101.Ledger.Contracts;

namespace Accounting101.ModuleKit;

/// <summary>
/// Pure comparison of a module's declared <see cref="AccountRequirement"/>s against a client's chart.
/// A required account must exist at its id, be Active, be the expected type (when declared), and — for
/// folded accounts — carry the required dimensions (subset: the account may require more). Reports the
/// most fundamental problem first (Missing → Inactive → WrongType → MissingDimensions → Ok).
/// </summary>
public static class ChartReadinessChecker
{
    public static ChartReadinessReport Check(
        IReadOnlyList<AccountRequirement> requirements,
        IReadOnlyList<AccountResponse> chart,
        string moduleKey)
    {
        Dictionary<Guid, AccountResponse> byId = chart.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        List<AccountReadinessResult> results = requirements.Select(req => Evaluate(req, byId)).ToList();
        return new ChartReadinessReport(moduleKey, results.All(r => r.Status == AccountReadinessStatus.Ok), results);
    }

    private static AccountReadinessResult Evaluate(AccountRequirement req, IReadOnlyDictionary<Guid, AccountResponse> byId)
    {
        if (!byId.TryGetValue(req.AccountId, out AccountResponse? a))
            return Result(req, AccountReadinessStatus.Missing, null, null,
                $"No account with id {req.AccountId} ('{req.Label}') exists in the chart.");

        if (!a.Active)
            return Result(req, AccountReadinessStatus.Inactive, a.Type, a.RequiredDimensions,
                $"Account '{req.Label}' ({a.Number}) exists but is inactive.");

        if (req.ExpectedType is { } expected && !string.Equals(a.Type, expected, StringComparison.OrdinalIgnoreCase))
            return Result(req, AccountReadinessStatus.WrongType, a.Type, a.RequiredDimensions,
                $"Account '{req.Label}' ({a.Number}) is {a.Type}, expected {expected}.");

        List<string> missing = req.RequiredDimensions.Where(d => !a.RequiredDimensions.Contains(d)).ToList();
        if (missing.Count > 0)
            return Result(req, AccountReadinessStatus.MissingDimensions, a.Type, a.RequiredDimensions,
                $"Account '{req.Label}' ({a.Number}) must require the " +
                $"{string.Join(", ", missing.Select(d => $"'{d}'"))} dimension(s) for the module's fold.");

        return Result(req, AccountReadinessStatus.Ok, a.Type, a.RequiredDimensions, "OK.");
    }

    private static AccountReadinessResult Result(
        AccountRequirement req, AccountReadinessStatus status,
        string? actualType, IReadOnlyList<string>? actualDims, string detail) =>
        new(req.AccountId, req.Label, req.ExpectedType, req.RequiredDimensions, status, actualType, actualDims, detail);
}
