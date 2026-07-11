using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;

namespace Accounting101.Payroll.Api;

/// <summary>The payroll HTTP surface: payroll-run and tax-remittance lifecycle under /clients/{clientId}.
/// Each record is one-step (record-and-post as PendingApproval — the module never self-approves) and
/// voidable. Responses are the domain types; only the request bodies are DTOs.</summary>
public static class PayrollEndpoints
{
    public static void MapPayrollEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/payroll-runs", RecordRun);
        clients.MapPost("/payroll-runs/{runId:guid}/void", VoidRun);
        clients.MapGet("/payroll-runs/{runId:guid}", GetRun);
        clients.MapGet("/payroll-runs", ListRuns);

        clients.MapPost("/tax-remittances", RecordRemittance);
        clients.MapPost("/tax-remittances/{remittanceId:guid}/void", VoidRemittance);
        clients.MapGet("/tax-remittances/{remittanceId:guid}", GetRemittance);
        clients.MapGet("/tax-remittances", ListRemittances);

        clients.MapGet("/payroll/chart-readiness", ChartReadiness);
    }

    // ── Payroll Runs ────────────────────────────────────────────────────────

    private static async Task<IResult> RecordRun(
        Guid clientId, RecordPayrollRunRequest request, PayrollService service, CancellationToken cancellationToken)
    {
        try
        {
            PayrollRun run = await service.RecordRunAsync(clientId,
                new PayrollRunBody(request.Gross, request.EmployeeFica, request.EmployerFica,
                    request.Deductions, request.IncomeTaxWithheld, request.PayDate, request.Memo),
                cancellationToken);
            return Results.Created($"/clients/{clientId}/payroll-runs/{run.Id}", run);
        }
        catch (ArgumentException ex) // negative derived net pay
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidRun(
        Guid clientId, Guid runId, VoidReasonRequest? request, PayrollService service, CancellationToken cancellationToken)
    {
        try
        {
            PayrollRun voided = await service.VoidRunAsync(clientId, runId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex) // not posted, not found, or no entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetRun(
        Guid clientId, Guid runId, PayrollService service, CancellationToken cancellationToken)
    {
        PayrollRun? run = await service.GetRunAsync(clientId, runId, cancellationToken);
        return run is null ? Results.NotFound() : Results.Ok(new PayrollRunView(run));
    }

    private static async Task<IResult> ListRuns(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        PayrollService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<PayrollRun> page = await service.ListRunsAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<PayrollRunView>(
            page.Items.Select(r => new PayrollRunView(r)).ToList(), page.Total, page.Skip, page.Limit));
    }

    // ── Tax Remittances ─────────────────────────────────────────────────────

    private static async Task<IResult> RecordRemittance(
        Guid clientId, RecordTaxRemittanceRequest request, PayrollService service, CancellationToken cancellationToken)
    {
        TaxRemittance remittance = await service.RecordRemittanceAsync(clientId,
            new TaxRemittanceBody(request.WithholdingsAmount, request.TaxesAmount, request.PayDate, request.Memo),
            cancellationToken);
        return Results.Created($"/clients/{clientId}/tax-remittances/{remittance.Id}", remittance);
    }

    private static async Task<IResult> VoidRemittance(
        Guid clientId, Guid remittanceId, VoidReasonRequest? request, PayrollService service, CancellationToken cancellationToken)
    {
        try
        {
            TaxRemittance voided = await service.VoidRemittanceAsync(clientId, remittanceId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex) // not posted, not found, or no entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetRemittance(
        Guid clientId, Guid remittanceId, PayrollService service, CancellationToken cancellationToken)
    {
        TaxRemittance? remittance = await service.GetRemittanceAsync(clientId, remittanceId, cancellationToken);
        return remittance is null ? Results.NotFound() : Results.Ok(new TaxRemittanceView(remittance));
    }

    private static async Task<IResult> ListRemittances(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        PayrollService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<TaxRemittance> page = await service.ListRemittancesAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<TaxRemittanceView>(
            page.Items.Select(r => new TaxRemittanceView(r)).ToList(), page.Total, page.Skip, page.Limit));
    }

    // ── Chart Readiness ─────────────────────────────────────────────────────

    private static async Task<IResult> ChartReadiness(
        Guid clientId, PayrollChartRequirements requirements, ILedgerClient ledger, CancellationToken cancellationToken)
    {
        CapabilitiesResponse caps = await ledger.GetMyCapabilitiesAsync(clientId, cancellationToken); // non-member → relayed 403
        if (!ReadinessAccess.Allows("payroll", caps.DeploymentAdmin, caps.Capabilities))
            return Results.Problem("Not authorized to view this module's chart readiness.", statusCode: StatusCodes.Status403Forbidden);

        IReadOnlyList<AccountRequirement> reqs = await requirements.ForAsync(clientId, cancellationToken);
        IReadOnlyList<AccountResponse> chart = await ledger.GetAccountsAsync(clientId, cancellationToken);
        return Results.Ok(ChartReadinessChecker.Check(reqs, chart, "payroll"));
    }

    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }
}
