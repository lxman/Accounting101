using System.Text.Json;
using Accounting101.Banking.Reconciliation;
using Accounting101.Interchange;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

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
        clients.MapPost("/bank-statements/import", ImportStatement).DisableAntiforgery();

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
        Guid clientId, Guid? cashAccountId, int? skip, int? limit, string? order,
        ReconciliationService service, CancellationToken ct)
    {
        if (cashAccountId is null || cashAccountId == Guid.Empty)
            return Results.Problem("cashAccountId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<BankStatement> all = await service.ListStatementsAsync(clientId, cashAccountId.Value, ct);
        IEnumerable<BankStatement> ordered = descending
            ? all.OrderByDescending(s => s.Number) : all.OrderBy(s => s.Number);
        List<BankStatement> items = ordered.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToList();
        return Results.Ok(new PagedResponse<BankStatement>(items, all.Count, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200)));
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
        catch (InvalidOperationException ex) // reconciliation not found / completed
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ListAdjustments(
        Guid clientId, Guid id, int? skip, int? limit, string? order,
        AdjustmentService service, CancellationToken ct)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<BankAdjustment> all = await service.ListAdjustmentsAsync(clientId, id, ct);
        IEnumerable<BankAdjustment> ordered = descending
            ? all.OrderByDescending(a => a.Number) : all.OrderBy(a => a.Number);
        List<BankAdjustment> items = ordered.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToList();
        return Results.Ok(new PagedResponse<BankAdjustment>(items, all.Count, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200)));
    }

    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }

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

    private static async Task<IResult> ImportStatement(
        Guid clientId, IFormFile? file, [FromForm] string? format, [FromForm] string? mapping,
        IInterchangeRegistry registry, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Results.Problem("A non-empty file is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (!Enum.TryParse(format, ignoreCase: true, out InterchangeFormat fmt))
            return Results.Problem($"Unsupported or missing format '{format}'.", statusCode: StatusCodes.Status400BadRequest);

        IImporter<ImportedStatement>? importer = registry.Resolve<ImportedStatement>(fmt);
        if (importer is null)
            return Results.Problem($"No statement importer is registered for format '{fmt}'.", statusCode: StatusCodes.Status400BadRequest);

        CsvMapping? csvMapping = null;
        if (fmt == InterchangeFormat.Csv)
        {
            if (string.IsNullOrWhiteSpace(mapping))
                return Results.Problem("A CSV 'mapping' is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
            try
            {
                csvMapping = JsonSerializer.Deserialize<CsvMapping>(mapping, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException ex)
            {
                return Results.Problem($"Invalid mapping JSON: {ex.Message}", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            if (csvMapping is null)
                return Results.Problem("A CSV 'mapping' is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            await using Stream stream = file.OpenReadStream();
            ImportResult<ImportedStatement> result = importer.Import(stream, new ImportOptions { Csv = csvMapping });
            List<StatementPreview> statements = result.Records
                .Select(s => new StatementPreview(
                    s.Lines.Select(l => new BankStatementLineRequest(l.Date, l.Amount, l.Description, l.Reference)).ToList(),
                    s.OpeningBalance, s.ClosingBalance, s.StatementDate, s.AccountHint))
                .ToList();
            return Results.Ok(new ImportPreviewResponse(statements, result.Warnings));
        }
        catch (ArgumentException ex) // invalid mapping (no amount columns, missing header, header-name without HasHeader)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (NotSupportedException ex) // e.g. OFX 2.x XML, not yet supported
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
