using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>Persists bill payments and vendor credit applications through the engine's document store as
/// evidentiary data: created, immediately finalized (locked — there is no draft for a payment), and
/// voidable. The module owns no database connection.</summary>
public sealed class DocumentBillPaymentStore(IDocumentStore documents) : IBillPaymentStore
{
    private const string BillPayments = "bill-payments";
    private const string VendorCreditApplications = "vendor-credit-applications";

    public async Task<BillPayment> RecordPaymentAsync(Guid clientId, BillPaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, BillPayments, body, Tags(body.VendorId), ct);
        await documents.FinalizeAsync(clientId, BillPayments, id, ct);
        DocumentResult<BillPaymentBody>? result = await documents.GetAsync<BillPaymentBody>(clientId, BillPayments, id, ct);
        return MapPayment(result!);
    }

    public async Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, VendorCreditApplications, body, Tags(body.VendorId), ct);
        await documents.FinalizeAsync(clientId, VendorCreditApplications, id, ct);
        DocumentResult<VendorCreditApplicationBody>? result = await documents.GetAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, id, ct);
        return MapCredit(result!);
    }

    public Task VoidAsync(Guid clientId, Guid paymentId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, BillPayments, paymentId, ct);

    public async Task<BillPayment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        DocumentResult<BillPaymentBody>? result = await documents.GetAsync<BillPaymentBody>(clientId, BillPayments, paymentId, ct);
        return result is null ? null : MapPayment(result);
    }

    public async Task<IReadOnlyList<BillPayment>> GetPaymentsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BillPaymentBody>> results =
            await documents.QueryAsync<BillPaymentBody>(clientId, BillPayments, Tags(vendorId), ct);
        return results.Select(MapPayment).ToList();
    }

    public async Task<IReadOnlyList<VendorCreditApplication>> GetCreditApplicationsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<VendorCreditApplicationBody>> results =
            await documents.QueryAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, Tags(vendorId), ct);
        return results.Select(MapCredit).ToList();
    }

    private static Dictionary<string, string> Tags(Guid vendorId) => new() { ["Vendor"] = vendorId.ToString() };

    private static bool IsVoided(DocumentLifecycle state) =>
        state is DocumentLifecycle.Voided or DocumentLifecycle.Superseded;

    private static BillPayment MapPayment(DocumentResult<BillPaymentBody> r) => new()
    {
        Id = r.Id, VendorId = r.Body.VendorId, Date = r.Body.Date, Amount = r.Body.Amount,
        Method = r.Body.Method, Allocations = r.Body.Allocations, Voided = IsVoided(r.State),
    };

    private static VendorCreditApplication MapCredit(DocumentResult<VendorCreditApplicationBody> r) => new()
    {
        Id = r.Id, VendorId = r.Body.VendorId, Date = r.Body.Date,
        Allocations = r.Body.Allocations, Voided = IsVoided(r.State),
    };
}
