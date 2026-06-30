using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Payables.Api;

/// <summary>The payables HTTP surface: vendor + bill lifecycle under /clients/{clientId}.
/// Responses are the domain types; only the request bodies are DTOs.</summary>
public static class PayablesEndpoints
{
    public static void MapPayablesEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/vendors", CreateVendor);
        clients.MapGet("/vendors", ListVendors);
        clients.MapPost("/bills", DraftBill);
        clients.MapPost("/bills/{billId:guid}/enter", EnterBill);
        clients.MapPost("/bills/{billId:guid}/void", VoidBill);
        clients.MapGet("/bills/{billId:guid}", GetBill);
        clients.MapGet("/bills", ListBills);
        clients.MapPost("/bill-payments", RecordPayment);
        clients.MapGet("/bill-payments", ListBillPayments);
        clients.MapPost("/bill-payments/{paymentId:guid}/void", VoidPayment);
        clients.MapPost("/vendor-credit-applications", ApplyCredit);
        clients.MapGet("/vendors/{vendorId:guid}/credit-balance", GetCreditBalance);
    }

    private static async Task<IResult> CreateVendor(
        Guid clientId, CreateVendorRequest request, IVendorStore store, CancellationToken cancellationToken)
    {
        Vendor vendor = new() { Id = Guid.NewGuid(), Name = request.Name, Email = request.Email };
        await store.SaveAsync(clientId, vendor, cancellationToken);
        return Results.Created($"/clients/{clientId}/vendors/{vendor.Id}", vendor);
    }

    private static async Task<IResult> ListVendors(
        Guid clientId, IVendorStore store, CancellationToken cancellationToken) =>
        Results.Ok(await store.ListAsync(clientId, cancellationToken));

    private static async Task<IResult> DraftBill(
        Guid clientId, DraftBillRequest request, BillService service, CancellationToken cancellationToken)
    {
        try
        {
            Bill draft = await service.DraftAsync(
                clientId,
                new BillBody(request.VendorId, request.BillDate, request.DueDate, request.VendorReference, request.Memo, request.Lines),
                cancellationToken);
            return Results.Created($"/clients/{clientId}/bills/{draft.Id}", draft);
        }
        catch (InvalidOperationException ex) // unknown vendor, or no lines
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> EnterBill(
        Guid clientId, Guid billId, BillService service, CancellationToken cancellationToken)
    {
        try
        {
            Bill entered = await service.EnterAsync(clientId, billId, cancellationToken);
            return Results.Ok(entered);
        }
        catch (InvalidOperationException ex) // not a draft, total <= 0, or not found
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }

    private static async Task<IResult> VoidBill(
        Guid clientId, Guid billId, VoidReasonRequest? request, BillService service, CancellationToken cancellationToken)
    {
        try
        {
            Bill voided = await service.VoidAsync(clientId, billId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex) // not entered, not found, or no posted entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }

    private static async Task<IResult> GetBill(
        Guid clientId, Guid billId, BillPaymentService payments, CancellationToken cancellationToken)
    {
        BillView? view = await payments.GetBillViewAsync(clientId, billId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }

    private static async Task<IResult> ListBills(
        Guid clientId, Guid? vendorId, string? settlement, int? skip, int? limit, string? order,
        BillPaymentService service, CancellationToken cancellationToken)
    {
        if (vendorId is null || vendorId == Guid.Empty)
            return Results.Problem("vendorId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);

        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        SettlementFilter? filter;
        switch (settlement?.ToLowerInvariant())
        {
            case null or "": filter = null; break;
            case "open": filter = SettlementFilter.Open; break;
            case "paid": filter = SettlementFilter.Paid; break;
            default: return Results.Problem($"Unknown settlement filter '{settlement}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        IReadOnlyList<BillView> all = await service.ListBillViewsAsync(clientId, vendorId.Value, filter, cancellationToken);
        IEnumerable<BillView> ordered = descending
            ? all.OrderByDescending(v => v.Bill.Number) : all.OrderBy(v => v.Bill.Number);
        List<BillView> items = ordered.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToList();
        return Results.Ok(new PagedResponse<BillView>(items, all.Count, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200)));
    }

    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }

    private static async Task<IResult> ListBillPayments(
        Guid clientId, Guid? vendorId, IBillPaymentStore store, CancellationToken cancellationToken)
    {
        if (vendorId is null || vendorId == Guid.Empty)
            return Results.Problem("vendorId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<BillPayment> payments = await store.GetPaymentsByVendorAsync(clientId, vendorId.Value, cancellationToken);
        return Results.Ok(payments);
    }

    private static async Task<IResult> RecordPayment(
        Guid clientId, RecordBillPaymentRequest request, BillPaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            BillPayment recorded = await service.RecordPaymentAsync(clientId,
                new BillPaymentBody(request.VendorId, request.Date, request.Amount, request.Method, request.Allocations),
                cancellationToken);
            return Results.Created($"/clients/{clientId}/bill-payments/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }

    private static async Task<IResult> VoidPayment(
        Guid clientId, Guid paymentId, VoidReasonRequest? request, BillPaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            BillPayment voided = await service.VoidPaymentAsync(clientId, paymentId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }

    private static async Task<IResult> ApplyCredit(
        Guid clientId, VendorCreditApplicationRequest request, BillPaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            VendorCreditApplication applied = await service.RecordCreditApplicationAsync(clientId,
                new VendorCreditApplicationBody(request.VendorId, request.Date, request.Allocations), cancellationToken);
            return Results.Created($"/clients/{clientId}/vendor-credit-applications/{applied.Id}", applied);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }

    private static async Task<IResult> GetCreditBalance(
        Guid clientId, Guid vendorId, BillPaymentService service, CancellationToken cancellationToken)
    {
        decimal balance = await service.GetVendorCreditBalanceAsync(clientId, vendorId, cancellationToken);
        return Results.Ok(new { vendorId, creditBalance = balance });
    }
}
