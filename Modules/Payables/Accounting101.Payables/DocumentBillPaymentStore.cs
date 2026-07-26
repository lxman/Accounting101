using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>Persists bill payments and vendor credit applications through the engine's document store as
/// evidentiary data: created, immediately finalized (locked — there is no draft for a payment), and
/// voidable. The module owns no database connection.</summary>
public sealed class DocumentBillPaymentStore(IDocumentStore documents) : IBillPaymentStore
{
    private const string BillPayments = "bill-payments";
    private const string VendorCreditApplications = "vendor-credit-applications";

    public async Task<(BillPayment Payment, bool Created)> RecordPaymentAsync(Guid clientId, Guid id, BillPaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the caller that actually created the document finalizes it. FinalizeAsync throws on a
        // document that is no longer Draft (a deliberately pinned invariant), and that exception type
        // lives in the engine's Api assembly where this module cannot catch it — so we must not race.
        // This also means a replay burns no sequence number.
        //
        // CreateWithIdAsync's bool is the ONLY reliable signal of who actually won a race on this id: two
        // concurrent callers can both see "not found" on an earlier existence check (that check is
        // inherently TOCTOU under concurrency), but the engine's insert-only create is atomic, so exactly
        // one caller gets true here. The caller (BillPaymentService) uses this to decide replay vs. create
        // for ITS OWN response, rather than trusting its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, BillPayments, id, body, Tags(body.VendorId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, BillPayments, id, ct);
        DocumentResult<BillPaymentBody>? result = await documents.GetAsync<BillPaymentBody>(clientId, BillPayments, id, ct);
        return (MapPayment(result!), created);
    }

    public async Task<(VendorCreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the caller that actually created the document finalizes it. FinalizeAsync throws on a
        // document that is no longer Draft (a deliberately pinned invariant), and that exception type
        // lives in the engine's Api assembly where this module cannot catch it — so we must not race.
        // This also means a replay burns no sequence number.
        //
        // CreateWithIdAsync's bool is the ONLY reliable signal of who won a race on this id: two concurrent
        // callers can both see "not found" on an earlier existence check (that check is inherently TOCTOU),
        // but the engine's insert-only create is atomic, so exactly one caller gets true here.
        bool created = await documents.CreateWithIdAsync(clientId, VendorCreditApplications, id, body, Tags(body.VendorId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, VendorCreditApplications, id, ct);
        DocumentResult<VendorCreditApplicationBody>? result = await documents.GetAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, id, ct);
        return (MapCredit(result!), created);
    }

    public Task VoidAsync(Guid clientId, Guid paymentId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, BillPayments, paymentId, ct);

    public async Task<BillPayment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        DocumentResult<BillPaymentBody>? result = await documents.GetAsync<BillPaymentBody>(clientId, BillPayments, paymentId, ct);
        return result is null ? null : MapPayment(result);
    }

    // Voided payments are included (greyed in the UI, per BillPaymentService's list-view docs) — the
    // caller/service filters, not this store — so includeVoided: true here, matching what the module
    // has always promised for this list.
    public async Task<IReadOnlyList<BillPayment>> GetPaymentsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BillPaymentBody>> results =
            await documents.QueryAsync<BillPaymentBody>(clientId, BillPayments, Tags(vendorId), includeVoided: true, cancellationToken: ct);
        return results.Select(MapPayment).ToList();
    }

    // Voided credit applications are included (greyed in the UI, per BillPaymentService's list-view docs) —
    // the caller/service filters, not this store — so includeVoided: true here, matching the sibling above.
    public async Task<IReadOnlyList<VendorCreditApplication>> GetCreditApplicationsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<VendorCreditApplicationBody>> results =
            await documents.QueryAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, Tags(vendorId), includeVoided: true, cancellationToken: ct);
        return results.Select(MapCredit).ToList();
    }

    public async Task<VendorCreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        DocumentResult<VendorCreditApplicationBody>? r = await documents.GetAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, creditApplicationId, ct);
        return r is null ? null : MapCredit(r);
    }

    private static Dictionary<string, string> Tags(Guid vendorId) => new() { ["Vendor"] = vendorId.ToString() };

    private static bool IsVoided(DocumentLifecycle state) =>
        state is DocumentLifecycle.Voided or DocumentLifecycle.Superseded;

    private static BillPayment MapPayment(DocumentResult<BillPaymentBody> r) => new()
    {
        Id = r.Id, VendorId = r.Body.VendorId, Date = r.Body.Date, Amount = r.Body.Amount,
        Method = r.Body.Method, Voided = IsVoided(r.State),
    };

    private static VendorCreditApplication MapCredit(DocumentResult<VendorCreditApplicationBody> r) => new()
    {
        Id = r.Id, VendorId = r.Body.VendorId, Date = r.Body.Date, Voided = IsVoided(r.State),
    };
}
