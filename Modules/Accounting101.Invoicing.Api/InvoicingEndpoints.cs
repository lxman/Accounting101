using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Api;

/// <summary>The invoicing HTTP surface: customer + invoice lifecycle under /clients/{clientId}.
/// Responses are the domain types; only the request bodies are DTOs.</summary>
public static class InvoicingEndpoints
{
    public static void MapInvoicingEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/customers", CreateCustomer);
        clients.MapPost("/invoices", DraftInvoice);
        clients.MapPost("/invoices/{invoiceId:guid}/issue", IssueInvoice);
        clients.MapPost("/invoices/{invoiceId:guid}/void", VoidInvoice);
        clients.MapGet("/invoices/{invoiceId:guid}", GetInvoice);
    }

    private static async Task<IResult> CreateCustomer(
        Guid clientId, CreateCustomerRequest request, InvoiceService service, CancellationToken cancellationToken)
    {
        Customer customer = await service.CreateCustomerAsync(clientId, request.Name, request.Email, cancellationToken);
        return Results.Created($"/clients/{clientId}/customers/{customer.Id}", customer);
    }

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
        Guid clientId, Guid invoiceId, InvoiceService service, CancellationToken cancellationToken)
    {
        Invoice? invoice = await service.GetAsync(clientId, invoiceId, cancellationToken);
        return invoice is null ? Results.NotFound() : Results.Ok(invoice);
    }
}
