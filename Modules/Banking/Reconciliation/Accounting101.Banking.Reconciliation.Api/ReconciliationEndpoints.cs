using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>The reconciliation HTTP surface under /clients/{clientId}: bank statements (record/read) and
/// reconciliations (start/worksheet/clear/unclear/complete). Read-only on the ledger.</summary>
public static class ReconciliationEndpoints
{
    public static void MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/bank-statements", RecordStatement);
        clients.MapGet("/bank-statements/{id:guid}", GetStatement);
        clients.MapGet("/bank-statements", ListStatements);

        clients.MapPost("/reconciliations", StartReconciliation);
        clients.MapGet("/reconciliations/{id:guid}", GetWorksheet);
        clients.MapPost("/reconciliations/{id:guid}/clear", Clear);
        clients.MapPost("/reconciliations/{id:guid}/unclear", Unclear);
        clients.MapPost("/reconciliations/{id:guid}/complete", Complete);
        clients.MapPost("/reconciliations/{id:guid}/auto-match", AutoMatch);

        clients.MapPost("/reconciliations/{id:guid}/adjustments", RecordAdjustment);
        clients.MapGet("/reconciliations/{id:guid}/adjustments", ListAdjustments);
        clients.MapGet("/reconciliations/{id:guid}/adjustments/{adjId:guid}", GetAdjustment);
        clients.MapPost("/reconciliations/{id:guid}/adjustments/{adjId:guid}/void", VoidAdjustment);
    }

    private static async Task<IResult> RecordStatement(
        Guid clientId, RecordBankStatementRequest request, ReconciliationService service, CancellationToken ct)
    {
        try
        {
            BankStatement statement = await service.RecordStatementAsync(clientId,
                new BankStatementBody(request.CashAccountId, request.StatementDate, request.OpeningBalance, request.ClosingBalance,
                    request.Lines.Select(l => new BankStatementLine(l.Date, l.Amount, l.Description, l.ExternalRef)).ToList()),
                ct);
            return Results.Created($"/clients/{clientId}/bank-statements/{statement.Id}", statement);
        }
        catch (ArgumentException ex) // empty lines, or statement does not foot
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetStatement(Guid clientId, Guid id, ReconciliationService service, CancellationToken ct)
    {
        BankStatement? statement = await service.GetStatementAsync(clientId, id, ct);
        return statement is null ? Results.NotFound() : Results.Ok(statement);
    }

    private static async Task<IResult> ListStatements(
        Guid clientId, Guid? cashAccountId, ReconciliationService service, CancellationToken ct)
    {
        if (cashAccountId is null || cashAccountId == Guid.Empty)
            return Results.Problem("cashAccountId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        return Results.Ok(await service.ListStatementsAsync(clientId, cashAccountId.Value, ct));
    }

    private static async Task<IResult> StartReconciliation(
        Guid clientId, StartReconciliationRequest request, ReconciliationService service, CancellationToken ct)
    {
        try
        {
            Reconciliation reconciliation = await service.StartReconciliationAsync(clientId, request.BankStatementId, ct);
            return Results.Created($"/clients/{clientId}/reconciliations/{reconciliation.Id}", reconciliation);
        }
        catch (ArgumentException ex) // unknown statement
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetWorksheet(Guid clientId, Guid id, ReconciliationService service, CancellationToken ct)
    {
        ReconciliationWorksheet? worksheet = await service.GetWorksheetAsync(clientId, id, ct);
        return worksheet is null ? Results.NotFound() : Results.Ok(worksheet);
    }

    private static Task<IResult> Clear(Guid clientId, Guid id, ClearRequest request, ReconciliationService service, CancellationToken ct) =>
        MutateAsync(() => service.ClearAsync(clientId, id, request.EntryIds, ct));

    private static Task<IResult> Unclear(Guid clientId, Guid id, ClearRequest request, ReconciliationService service, CancellationToken ct) =>
        MutateAsync(() => service.UnclearAsync(clientId, id, request.EntryIds, ct));

    private static async Task<IResult> MutateAsync(Func<Task<ReconciliationWorksheet>> op)
    {
        try { return Results.Ok(await op()); }
        catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }

    private static async Task<IResult> Complete(Guid clientId, Guid id, ReconciliationService service, CancellationToken ct)
    {
        try { return Results.Ok(await service.CompleteAsync(clientId, id, ct)); }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }

    private static async Task<IResult> RecordAdjustment(
        Guid clientId, Guid id, RecordAdjustmentRequest request, AdjustmentService service, CancellationToken ct)
    {
        try
        {
            BankAdjustment adjustment = await service.RecordAdjustmentAsync(clientId, id,
                new RecordAdjustmentInput(request.OffsetAccountId, request.Amount, request.Kind, request.Date, request.Memo), ct);
            return Results.Created($"/clients/{clientId}/reconciliations/{id}/adjustments/{adjustment.Id}", adjustment);
        }
        catch (ArgumentException ex) // amount <= 0, offset == cash
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (LedgerClientException ex) // engine rejected the post (closed period, unknown account)
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
        catch (InvalidOperationException ex) // reconciliation not found / completed
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ListAdjustments(Guid clientId, Guid id, AdjustmentService service, CancellationToken ct) =>
        Results.Ok(await service.ListAdjustmentsAsync(clientId, id, ct));

    private static async Task<IResult> GetAdjustment(Guid clientId, Guid id, Guid adjId, AdjustmentService service, CancellationToken ct)
    {
        BankAdjustment? adjustment = await service.GetAdjustmentAsync(clientId, adjId, ct);
        return adjustment is null ? Results.NotFound() : Results.Ok(adjustment);
    }

    private static async Task<IResult> VoidAdjustment(
        Guid clientId, Guid id, Guid adjId, VoidReasonRequest? request, AdjustmentService service, CancellationToken ct)
    {
        try
        {
            BankAdjustment voided = await service.VoidAdjustmentAsync(clientId, adjId, request?.Reason, ct);
            return Results.Ok(voided);
        }
        catch (LedgerClientException ex)
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
        catch (InvalidOperationException ex) // not found, already void, or no entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> AutoMatch(
        Guid clientId, Guid id, bool? apply, ReconciliationService service, CancellationToken ct)
    {
        try
        {
            return apply == true
                ? Results.Ok(await service.AutoMatchApplyAsync(clientId, id, ct))   // clears matches → worksheet
                : Results.Ok(await service.AutoMatchAsync(clientId, id, ct));        // read-only proposal
        }
        catch (InvalidOperationException ex) // reconciliation not found or already completed
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex) // ineligible id on the apply→clear path (not expected in normal operation)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
