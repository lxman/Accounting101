using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>Persists payments and credit applications through the engine's document store as evidentiary
/// data: created, immediately finalized (locked — there is no draft for a payment), and voidable. The
/// module owns no database connection.</summary>
public sealed class DocumentPaymentStore(IDocumentStore documents) : IPaymentStore
{
    private const string Payments = "payments";
    private const string CreditApplications = "credit-applications";

    public async Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Payments, body, Tags(body.CustomerId), ct);
        await documents.FinalizeAsync(clientId, Payments, id, ct);
        DocumentResult<PaymentBody>? result = await documents.GetAsync<PaymentBody>(clientId, Payments, id, ct);
        return MapPayment(result!);
    }

    public async Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, CreditApplications, body, Tags(body.CustomerId), ct);
        await documents.FinalizeAsync(clientId, CreditApplications, id, ct);
        DocumentResult<CreditApplicationBody>? result = await documents.GetAsync<CreditApplicationBody>(clientId, CreditApplications, id, ct);
        return MapCredit(result!);
    }

    public Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Payments, documentId, ct);

    public async Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        DocumentResult<PaymentBody>? result = await documents.GetAsync<PaymentBody>(clientId, Payments, paymentId, ct);
        return result is null ? null : MapPayment(result);
    }

    public async Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<PaymentBody>> results =
            await documents.QueryAsync<PaymentBody>(clientId, Payments, Tags(customerId), ct);
        return results.Select(MapPayment).ToList();
    }

    public async Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<CreditApplicationBody>> results =
            await documents.QueryAsync<CreditApplicationBody>(clientId, CreditApplications, Tags(customerId), ct);
        return results.Select(MapCredit).ToList();
    }

    private static Dictionary<string, string> Tags(Guid customerId) => new() { ["Customer"] = customerId.ToString() };

    private static bool IsVoided(DocumentLifecycle state) =>
        state is DocumentLifecycle.Voided or DocumentLifecycle.Superseded;

    private static Payment MapPayment(DocumentResult<PaymentBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date, Amount = r.Body.Amount,
        Method = r.Body.Method, Allocations = r.Body.Allocations, Voided = IsVoided(r.State),
    };

    private static CreditApplication MapCredit(DocumentResult<CreditApplicationBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Allocations = r.Body.Allocations, Voided = IsVoided(r.State),
    };
}
