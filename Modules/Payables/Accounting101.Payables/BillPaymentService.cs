using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>The cash-disbursement lifecycle: record a bill payment (allocate across bills, hold over-payment
/// as vendor credit), apply existing vendor credit, and void. Each document posts one balanced entry that
/// lands PendingApproval — approval is the client's normal maker-checker flow. Open balances and vendor
/// credit are derived from stored allocations, never stored.</summary>
public sealed class BillPaymentService(
    IBillPaymentStore payments, IBillStore bills, IBillAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<BillPayment> RecordPaymentAsync(Guid clientId, BillPaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        if (body.Allocations.Sum(a => a.Amount) > body.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        await ValidateAllocationsAsync(clientId, body.VendorId, body.Allocations, ct);

        BillPayment recorded = await payments.RecordPaymentAsync(clientId, body, ct);
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBillPayment(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    public async Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Allocations.Count == 0 || body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        decimal applying = body.Allocations.Sum(a => a.Amount);
        decimal available = await GetVendorCreditBalanceAsync(clientId, body.VendorId, ct);
        if (applying > available)
            throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

        await ValidateAllocationsAsync(clientId, body.VendorId, body.Allocations, ct);

        VendorCreditApplication recorded = await payments.RecordCreditApplicationAsync(clientId, body, ct);
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeVendorCreditApplication(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    public async Task<BillPayment> VoidPaymentAsync(Guid clientId, Guid paymentId, string? reason = null, CancellationToken ct = default)
    {
        BillPayment payment = await payments.GetPaymentAsync(clientId, paymentId, ct)
            ?? throw new InvalidOperationException($"Payment {paymentId} not found.");
        if (payment.Voided)
            throw new InvalidOperationException($"Payment {paymentId} is already voided.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, paymentId, ct);
        EntryResponse settlement = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for payment {paymentId} to void.");

        if (settlement.Posting == "Posted")
            await ledger.ReverseAsync(clientId, settlement.Id, new ReverseRequest(payment.Date, reason ?? $"Voided payment {paymentId}"), ct);
        else
            await ledger.VoidAsync(clientId, settlement.Id, new VoidRequest(reason ?? $"Voided payment {paymentId}"), ct);

        await payments.VoidAsync(clientId, paymentId, ct);
        return (await payments.GetPaymentAsync(clientId, paymentId, ct))!;
    }

    public async Task<BillView?> GetBillViewAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        Bill? bill = await bills.GetAsync(clientId, billId, ct);
        if (bill is null) return null;
        decimal applied = await AppliedToBillAsync(clientId, bill.VendorId, billId, ct);
        return new BillView(bill, Accounting101.Settlement.Settlement.OpenBalance(bill.Total, applied), Accounting101.Settlement.Settlement.Status(bill.Total, applied));
    }

    public async Task<decimal> GetVendorCreditBalanceAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);
        decimal created = ps.Where(p => !p.Voided).Sum(p => p.Unapplied);
        decimal spent = cs.Where(c => !c.Voided).Sum(c => c.Applied);
        return created - spent;
    }

    public async Task<IReadOnlyList<BillView>> ListBillViewsAsync(Guid clientId, Guid vendorId, SettlementFilter? filter, CancellationToken ct = default)
    {
        IReadOnlyList<Bill> vendorBills = await bills.GetByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);

        Dictionary<Guid, decimal> applied = new();
        foreach (Allocation a in ps.Where(p => !p.Voided).SelectMany(p => p.Allocations)
                     .Concat(cs.Where(c => !c.Voided).SelectMany(c => c.Allocations)))
            applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;

        IEnumerable<BillView> views = vendorBills
            .Where(bill => bill.Status != BillStatus.Void)
            .Select(bill =>
            {
                decimal ap = applied.GetValueOrDefault(bill.Id);
                return new BillView(bill, Accounting101.Settlement.Settlement.OpenBalance(bill.Total, ap), Accounting101.Settlement.Settlement.Status(bill.Total, ap));
            });

        views = filter switch
        {
            SettlementFilter.Open => views.Where(v => v.SettlementStatus != SettlementStatus.Paid),
            SettlementFilter.Paid => views.Where(v => v.SettlementStatus == SettlementStatus.Paid),
            _ => views,
        };
        return views.ToList();
    }

    private async Task ValidateAllocationsAsync(Guid clientId, Guid vendorId, IReadOnlyList<Allocation> allocations, CancellationToken ct)
    {
        foreach (Allocation a in allocations)
        {
            Bill bill = await bills.GetAsync(clientId, a.TargetId, ct)
                ?? throw new InvalidOperationException($"Bill {a.TargetId} does not exist.");
            if (bill.Status == BillStatus.Void)
                throw new InvalidOperationException($"Bill {a.TargetId} is voided.");
            if (bill.VendorId != vendorId)
                throw new InvalidOperationException($"Bill {a.TargetId} belongs to a different vendor.");

            decimal alreadyApplied = await AppliedToBillAsync(clientId, vendorId, a.TargetId, ct);
            if (alreadyApplied + a.Amount > bill.Total)
                throw new InvalidOperationException($"Allocation to bill {a.TargetId} exceeds its open balance.");
        }
    }

    private async Task<decimal> AppliedToBillAsync(Guid clientId, Guid vendorId, Guid billId, CancellationToken ct)
    {
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);
        decimal fromPayments = ps.Where(p => !p.Voided).SelectMany(p => p.Allocations).Where(x => x.TargetId == billId).Sum(x => x.Amount);
        decimal fromCredits = cs.Where(c => !c.Voided).SelectMany(c => c.Allocations).Where(x => x.TargetId == billId).Sum(x => x.Amount);
        return fromPayments + fromCredits;
    }
}
