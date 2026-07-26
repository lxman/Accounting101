using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>Persists payments and credit applications through the engine's document store as evidentiary
/// data: created, immediately finalized (locked — there is no draft for a payment), and voidable. The
/// module owns no database connection.</summary>
public sealed class DocumentPaymentStore(IDocumentStore documents) : IPaymentStore
{
    private const string Payments = "payments";
    private const string CreditApplications = "credit-applications";
    private const string WriteOffs = "write-offs";
    private const string CreditNotes = "credit-notes";
    private const string Refunds = "refunds";

    public async Task<(Payment Payment, bool Created)> RecordPaymentAsync(Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the caller that actually created the document finalizes it, so a replay burns no sequence
        // number and cannot race a concurrent creator.
        //
        // CreateWithIdAsync's bool is the ONLY reliable created-vs-replayed signal: two concurrent callers
        // can both see "not found" on an earlier existence check (inherently TOCTOU), but this create is
        // atomic, so exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Payments, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Payments, id, ct);
        DocumentResult<PaymentBody>? result = await documents.GetAsync<PaymentBody>(clientId, Payments, id, ct);
        return (MapPayment(result!), created);
    }

    public async Task<(CreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, CreditApplications, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, CreditApplications, id, ct);
        DocumentResult<CreditApplicationBody>? result = await documents.GetAsync<CreditApplicationBody>(clientId, CreditApplications, id, ct);
        return (MapCredit(result!), created);
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
            await documents.QueryAsync<PaymentBody>(clientId, Payments, Tags(customerId), includeVoided: true, cancellationToken: ct);
        return results.Select(MapPayment).ToList();
    }

    public async Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<CreditApplicationBody>> results =
            await documents.QueryAsync<CreditApplicationBody>(clientId, CreditApplications, Tags(customerId), includeVoided: true, cancellationToken: ct);
        return results.Select(MapCredit).ToList();
    }

    public async Task<CreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        DocumentResult<CreditApplicationBody>? r = await documents.GetAsync<CreditApplicationBody>(clientId, CreditApplications, creditApplicationId, ct);
        return r is null ? null : MapCredit(r);
    }

    public async Task<(WriteOff WriteOff, bool Created)> RecordWriteOffAsync(Guid clientId, Guid id, WriteOffBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, WriteOffs, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, WriteOffs, id, ct);
        DocumentResult<WriteOffBody>? r = await documents.GetAsync<WriteOffBody>(clientId, WriteOffs, id, ct);
        return (MapWriteOff(r!), created);
    }

    public async Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default)
    {
        DocumentResult<WriteOffBody>? r = await documents.GetAsync<WriteOffBody>(clientId, WriteOffs, writeOffId, ct);
        return r is null ? null : MapWriteOff(r);
    }

    public async Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<WriteOffBody>> rs = await documents.QueryAsync<WriteOffBody>(clientId, WriteOffs, Tags(customerId), includeVoided: true, cancellationToken: ct);
        return rs.Select(MapWriteOff).ToList();
    }

    public Task VoidWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, WriteOffs, writeOffId, ct);

    public async Task<(CreditNote CreditNote, bool Created)> RecordCreditNoteAsync(Guid clientId, Guid id, CreditNoteBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, CreditNotes, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, CreditNotes, id, ct);
        DocumentResult<CreditNoteBody>? r = await documents.GetAsync<CreditNoteBody>(clientId, CreditNotes, id, ct);
        return (MapCreditNote(r!), created);
    }

    public async Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default)
    {
        DocumentResult<CreditNoteBody>? r = await documents.GetAsync<CreditNoteBody>(clientId, CreditNotes, creditNoteId, ct);
        return r is null ? null : MapCreditNote(r);
    }

    public async Task<IReadOnlyList<CreditNote>> GetCreditNotesByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<CreditNoteBody>> rs = await documents.QueryAsync<CreditNoteBody>(clientId, CreditNotes, Tags(customerId), includeVoided: true, cancellationToken: ct);
        return rs.Select(MapCreditNote).ToList();
    }

    public Task VoidCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, CreditNotes, creditNoteId, ct);

    public async Task<(Refund Refund, bool Created)> RecordRefundAsync(Guid clientId, Guid id, RefundBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, Refunds, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Refunds, id, ct);
        DocumentResult<RefundBody>? r = await documents.GetAsync<RefundBody>(clientId, Refunds, id, ct);
        return (MapRefund(r!), created);
    }

    public async Task<Refund?> GetRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default)
    {
        DocumentResult<RefundBody>? r = await documents.GetAsync<RefundBody>(clientId, Refunds, refundId, ct);
        return r is null ? null : MapRefund(r);
    }

    public async Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<RefundBody>> rs = await documents.QueryAsync<RefundBody>(clientId, Refunds, Tags(customerId), includeVoided: true, cancellationToken: ct);
        return rs.Select(MapRefund).ToList();
    }

    public Task VoidRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Refunds, refundId, ct);

    private static Dictionary<string, string> Tags(Guid customerId) => new() { ["Customer"] = customerId.ToString() };

    private static bool IsVoided(DocumentLifecycle state) =>
        state is DocumentLifecycle.Voided or DocumentLifecycle.Superseded;

    private static Payment MapPayment(DocumentResult<PaymentBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date, Amount = r.Body.Amount,
        Method = r.Body.Method, Voided = IsVoided(r.State),
    };

    private static CreditApplication MapCredit(DocumentResult<CreditApplicationBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date, Voided = IsVoided(r.State),
    };

    private static WriteOff MapWriteOff(DocumentResult<WriteOffBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Memo = r.Body.Memo, Voided = IsVoided(r.State),
    };

    private static CreditNote MapCreditNote(DocumentResult<CreditNoteBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Memo = r.Body.Memo, Voided = IsVoided(r.State),
    };

    private static Refund MapRefund(DocumentResult<RefundBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Amount = r.Body.Amount, Memo = r.Body.Memo, Voided = IsVoided(r.State),
    };
}
