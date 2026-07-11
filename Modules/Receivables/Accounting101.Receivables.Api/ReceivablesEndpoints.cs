using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Accounting101.Settlement;
using System.Globalization;

namespace Accounting101.Receivables.Api;

/// <summary>The receivables HTTP surface: customer + invoice lifecycle under /clients/{clientId}.
/// Responses are the domain types; only the request bodies are DTOs.</summary>
public static class ReceivablesEndpoints
{
    public static void MapReceivablesEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/customers", CreateCustomer);
        clients.MapGet("/customers", ListCustomers);
        clients.MapPost("/invoices", DraftInvoice);
        clients.MapPut("/invoices/{invoiceId:guid}", EditInvoice);
        clients.MapDelete("/invoices/{invoiceId:guid}", DiscardInvoice);
        clients.MapPost("/invoices/{invoiceId:guid}/issue", IssueInvoice);
        clients.MapPost("/invoices/{invoiceId:guid}/void", VoidInvoice);
        clients.MapGet("/invoices/{invoiceId:guid}", GetInvoice);
        clients.MapGet("/invoices", ListInvoices);
        clients.MapPost("/payments", RecordPayment);
        clients.MapGet("/payments", ListPayments);
        clients.MapPost("/payments/{paymentId:guid}/void", VoidPayment);
        clients.MapGet("/credits", ListCredits);
        clients.MapPost("/credit-applications", ApplyCredit);
        clients.MapGet("/customers/{customerId:guid}/credit-balance", GetCreditBalance);
        clients.MapGet("/customers/{customerId:guid}/account", GetCustomerAccount);
        clients.MapPost("/write-offs", RecordWriteOff);
        clients.MapPost("/write-offs/{writeOffId:guid}/void", VoidWriteOff);
        clients.MapPost("/credit-notes", RecordCreditNote);
        clients.MapPost("/credit-notes/{creditNoteId:guid}/void", VoidCreditNote);
        clients.MapPost("/refunds", RecordRefund);
        clients.MapGet("/refunds", ListRefunds);
        clients.MapPost("/refunds/{refundId:guid}/void", VoidRefund);

        clients.MapGet("/receivables/chart-readiness", ChartReadiness);
    }

    private static async Task<IResult> CreateCustomer(
        Guid clientId, CreateCustomerRequest request, InvoiceService service, CancellationToken cancellationToken)
    {
        Customer customer = await service.CreateCustomerAsync(clientId, request.Name, request.Email, cancellationToken);
        return Results.Created($"/clients/{clientId}/customers/{customer.Id}", customer);
    }

    private static async Task<IResult> ListCustomers(
        Guid clientId, InvoiceService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListCustomersAsync(clientId, cancellationToken));

    private static async Task<IResult> DraftInvoice(
        Guid clientId, DraftInvoiceRequest request, InvoiceService service, CancellationToken cancellationToken)
    {
        try
        {
            Invoice draft = await service.DraftAsync(
                clientId, request.CustomerId, request.Lines, request.TaxRate,
                request.IssueDate, request.DueDate, request.Memo, cancellationToken);
            return Results.Created($"/clients/{clientId}/invoices/{draft.Id}", draft);
        }
        catch (InvalidOperationException ex) // unknown customer, or no lines
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> EditInvoice(
        Guid clientId, Guid invoiceId, DraftInvoiceRequest request, InvoiceService service, CancellationToken cancellationToken)
    {
        try
        {
            Invoice updated = await service.EditDraftAsync(
                clientId, invoiceId, request.CustomerId, request.Lines, request.TaxRate,
                request.IssueDate, request.DueDate, request.Memo, cancellationToken);
            return Results.Ok(updated);
        }
        catch (InvalidOperationException ex) // not an editable draft, unknown customer, or no lines
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> DiscardInvoice(
        Guid clientId, Guid invoiceId, InvoiceService service, CancellationToken cancellationToken)
    {
        try
        {
            await service.DiscardDraftAsync(clientId, invoiceId, cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex) // not a discardable draft
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> IssueInvoice(
        Guid clientId, Guid invoiceId, InvoiceService service, CancellationToken cancellationToken)
    {
        try
        {
            Invoice issued = await service.IssueAsync(clientId, invoiceId, cancellationToken);
            return Results.Ok(issued);
        }
        catch (InvalidOperationException ex) // not a draft, total <= 0, or not found
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> VoidInvoice(
        Guid clientId, Guid invoiceId, VoidInvoiceRequest? request, InvoiceService service, CancellationToken cancellationToken)
    {
        try
        {
            Invoice voided = await service.VoidAsync(clientId, invoiceId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex) // not issued, not found, or no posted entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetInvoice(
        Guid clientId, Guid invoiceId, PaymentService payments, CancellationToken cancellationToken)
    {
        InvoiceView? view = await payments.GetInvoiceViewAsync(clientId, invoiceId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }

    private static async Task<IResult> ListInvoices(
        Guid clientId, Guid? customerId, string? settlement, int? skip, int? limit, string? order,
        PaymentService service, CancellationToken cancellationToken)
    {
        if (customerId is null || customerId == Guid.Empty)
            return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);

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

        IReadOnlyList<InvoiceView> all = await service.ListInvoiceViewsAsync(clientId, customerId.Value, filter, cancellationToken);
        IEnumerable<InvoiceView> ordered = descending
            ? all.OrderByDescending(v => v.Invoice.Number) : all.OrderBy(v => v.Invoice.Number);
        List<InvoiceView> items = ordered.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToList();
        return Results.Ok(new PagedResponse<InvoiceView>(items, all.Count, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200)));
    }

    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }

    private static async Task<IResult> ListPayments(
        Guid clientId, Guid? customerId, PaymentService service, CancellationToken cancellationToken)
    {
        if (customerId is null || customerId == Guid.Empty)
            return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<Payment> payments = await service.GetPaymentsByCustomerAsync(clientId, customerId.Value, cancellationToken);
        return Results.Ok(payments);
    }

    private static async Task<IResult> GetCustomerAccount(
        Guid clientId, Guid customerId, string? asOf, CustomerAccountService service, CancellationToken cancellationToken)
    {
        DateOnly date;
        if (string.IsNullOrEmpty(asOf))
            date = DateOnly.FromDateTime(DateTime.UtcNow);
        else if (!DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return Results.Problem("asOf must be a date (yyyy-MM-dd).", statusCode: StatusCodes.Status400BadRequest);

        CustomerAccountView? view = await service.GetAccountAsync(clientId, customerId, date, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }

    private static async Task<IResult> ListCredits(
        Guid clientId, Guid? customerId, PaymentService service, CancellationToken cancellationToken)
    {
        if (customerId is null || customerId == Guid.Empty)
            return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<CreditDocument> credits = await service.GetCreditsByCustomerAsync(clientId, customerId.Value, cancellationToken);
        return Results.Ok(credits);
    }

    private static async Task<IResult> ListRefunds(
        Guid clientId, Guid? customerId, PaymentService service, CancellationToken cancellationToken)
    {
        if (customerId is null || customerId == Guid.Empty)
            return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<Refund> refunds = await service.GetRefundsByCustomerAsync(clientId, customerId.Value, cancellationToken);
        return Results.Ok(refunds);
    }

    private static async Task<IResult> RecordPayment(
        Guid clientId, RecordPaymentRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            Payment recorded = await service.RecordPaymentAsync(clientId,
                new PaymentCommand(request.CustomerId, request.Date, request.Amount, request.Method, request.Allocations),
                cancellationToken);
            return Results.Created($"/clients/{clientId}/payments/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidPayment(
        Guid clientId, Guid paymentId, VoidInvoiceRequest? request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            Payment voided = await service.VoidPaymentAsync(clientId, paymentId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ApplyCredit(
        Guid clientId, CreditApplicationRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            CreditApplication applied = await service.RecordCreditApplicationAsync(clientId,
                new CreditApplicationCommand(request.CustomerId, request.Date, request.Allocations), cancellationToken);
            return Results.Created($"/clients/{clientId}/credit-applications/{applied.Id}", applied);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetCreditBalance(
        Guid clientId, Guid customerId, PaymentService service, CancellationToken cancellationToken)
    {
        decimal balance = await service.GetCustomerCreditBalanceAsync(clientId, customerId, cancellationToken);
        return Results.Ok(new { customerId, creditBalance = balance });
    }

    private static async Task<IResult> RecordWriteOff(
        Guid clientId, WriteOffRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            WriteOff recorded = await service.RecordWriteOffAsync(clientId,
                new WriteOffCommand(request.CustomerId, request.Date, request.Allocations, request.Memo), cancellationToken);
            return Results.Created($"/clients/{clientId}/write-offs/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidWriteOff(
        Guid clientId, Guid writeOffId, VoidInvoiceRequest? request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            WriteOff voided = await service.VoidWriteOffAsync(clientId, writeOffId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> RecordCreditNote(
        Guid clientId, CreditNoteRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            CreditNote recorded = await service.RecordCreditNoteAsync(clientId,
                new CreditNoteCommand(request.CustomerId, request.Date, request.Allocations, request.Memo), cancellationToken);
            return Results.Created($"/clients/{clientId}/credit-notes/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidCreditNote(
        Guid clientId, Guid creditNoteId, VoidInvoiceRequest? request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            CreditNote voided = await service.VoidCreditNoteAsync(clientId, creditNoteId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> RecordRefund(
        Guid clientId, RefundRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            Refund recorded = await service.RecordRefundAsync(clientId,
                new RefundBody(request.CustomerId, request.Date, request.Amount, request.Memo), cancellationToken);
            return Results.Created($"/clients/{clientId}/refunds/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidRefund(
        Guid clientId, Guid refundId, VoidInvoiceRequest? request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            Refund voided = await service.VoidRefundAsync(clientId, refundId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // ── Chart Readiness ─────────────────────────────────────────────────────

    private static async Task<IResult> ChartReadiness(
        Guid clientId, ReceivablesChartRequirements requirements, ILedgerClient ledger, CancellationToken cancellationToken)
    {
        CapabilitiesResponse caps = await ledger.GetMyCapabilitiesAsync(clientId, cancellationToken); // non-member → relayed 403
        if (!ReadinessAccess.Allows("receivables", caps.DeploymentAdmin, caps.Capabilities))
            return Results.Problem("Not authorized to view this module's chart readiness.", statusCode: StatusCodes.Status403Forbidden);

        IReadOnlyList<AccountRequirement> reqs = await requirements.ForAsync(clientId, cancellationToken);
        IReadOnlyList<AccountResponse> chart = await ledger.GetAccountsAsync(clientId, cancellationToken);
        return Results.Ok(ChartReadinessChecker.Check(reqs, chart, "receivables"));
    }
}
