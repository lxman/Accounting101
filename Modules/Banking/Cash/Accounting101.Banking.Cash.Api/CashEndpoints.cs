using Accounting101.Banking.Cash;

namespace Accounting101.Banking.Cash.Api;

/// <summary>The cash HTTP surface: disbursement and deposit lifecycle under /clients/{clientId}.
/// Each record is one-step (record-and-post as PendingApproval — the module never self-approves) and
/// voidable. Responses are the domain types; only the request bodies are DTOs.</summary>
public static class CashEndpoints
{
    public static void MapCashEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/cash-disbursements", RecordDisbursement);
        clients.MapPost("/cash-disbursements/{id:guid}/void", VoidDisbursement);
        clients.MapGet("/cash-disbursements/{id:guid}", GetDisbursement);
        clients.MapGet("/cash-disbursements", ListDisbursements);

        clients.MapPost("/cash-deposits", RecordDeposit);
        clients.MapPost("/cash-deposits/{id:guid}/void", VoidDeposit);
        clients.MapGet("/cash-deposits/{id:guid}", GetDeposit);
        clients.MapGet("/cash-deposits", ListDeposits);
    }

    // ── Cash Disbursements ──────────────────────────────────────────────────

    private static async Task<IResult> RecordDisbursement(
        Guid clientId, RecordCashDisbursementRequest request, CashService service, CancellationToken cancellationToken)
    {
        try
        {
            CashDisbursement disbursement = await service.RecordDisbursementAsync(clientId,
                new CashDisbursementBody(
                    Lines: request.Lines.Select(l => new CashLine(l.AccountId, l.Amount)).ToList(),
                    Date: request.Date,
                    Reference: request.Reference,
                    Memo: request.Memo),
                cancellationToken);
            return Results.Created($"/clients/{clientId}/cash-disbursements/{disbursement.Id}", disbursement);
        }
        catch (ArgumentException ex) // empty lines, non-positive amount, or cash account in lines
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidDisbursement(
        Guid clientId, Guid id, VoidReasonRequest? request, CashService service, CancellationToken cancellationToken)
    {
        try
        {
            CashDisbursement voided = await service.VoidDisbursementAsync(clientId, id, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex) // not posted, not found, or no entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetDisbursement(
        Guid clientId, Guid id, CashService service, CancellationToken cancellationToken)
    {
        CashDisbursement? disbursement = await service.GetDisbursementAsync(clientId, id, cancellationToken);
        return disbursement is null ? Results.NotFound() : Results.Ok(new CashDisbursementView(disbursement));
    }

    private static async Task<IResult> ListDisbursements(
        Guid clientId, ICashDisbursementStore store, CancellationToken cancellationToken)
    {
        IReadOnlyList<CashDisbursement> disbursements = await store.GetByClientAsync(clientId, cancellationToken);
        return Results.Ok(disbursements.Select(d => new CashDisbursementView(d)).ToList());
    }

    // ── Cash Deposits ───────────────────────────────────────────────────────

    private static async Task<IResult> RecordDeposit(
        Guid clientId, RecordCashDepositRequest request, CashService service, CancellationToken cancellationToken)
    {
        try
        {
            CashDeposit deposit = await service.RecordDepositAsync(clientId,
                new CashDepositBody(
                    Lines: request.Lines.Select(l => new CashLine(l.AccountId, l.Amount)).ToList(),
                    Date: request.Date,
                    Reference: request.Reference,
                    Memo: request.Memo),
                cancellationToken);
            return Results.Created($"/clients/{clientId}/cash-deposits/{deposit.Id}", deposit);
        }
        catch (ArgumentException ex) // empty lines, non-positive amount, or cash account in lines
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidDeposit(
        Guid clientId, Guid id, VoidReasonRequest? request, CashService service, CancellationToken cancellationToken)
    {
        try
        {
            CashDeposit voided = await service.VoidDepositAsync(clientId, id, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex) // not posted, not found, or no entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetDeposit(
        Guid clientId, Guid id, CashService service, CancellationToken cancellationToken)
    {
        CashDeposit? deposit = await service.GetDepositAsync(clientId, id, cancellationToken);
        return deposit is null ? Results.NotFound() : Results.Ok(new CashDepositView(deposit));
    }

    private static async Task<IResult> ListDeposits(
        Guid clientId, ICashDepositStore store, CancellationToken cancellationToken)
    {
        IReadOnlyList<CashDeposit> deposits = await store.GetByClientAsync(clientId, cancellationToken);
        return Results.Ok(deposits.Select(d => new CashDepositView(d)).ToList());
    }
}
