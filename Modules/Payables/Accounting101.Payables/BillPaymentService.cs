using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>The cash-disbursement lifecycle: record a bill payment (allocate across bills, hold over-payment
/// as vendor credit), apply existing vendor credit, and void. Each document posts one balanced entry that
/// lands PendingApproval — approval is the client's normal maker-checker flow. Open balances and vendor
/// credit are folded from the ledger's Bill/Vendor-axis subledgers on every read.</summary>
public sealed class BillPaymentService(
    IBillPaymentStore payments, IBillStore bills, IBillAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<BillPayment> RecordPaymentAsync(Guid clientId, BillPaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        if (command.Allocations.Sum(a => a.Amount) > command.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        await ValidateAllocationsAsync(clientId, command.VendorId, command.Allocations, ct);

        BillPaymentBody body = new(command.VendorId, command.Date, command.Amount, command.Method);
        BillPayment recorded = await payments.RecordPaymentAsync(clientId, body, ct);
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBillPayment(recorded.Id, body, command.Allocations, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    public async Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        decimal applying = command.Allocations.Sum(a => a.Amount);
        decimal available = await GetVendorCreditBalanceAsync(clientId, command.VendorId, ct);
        if (applying > available)
            throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

        await ValidateAllocationsAsync(clientId, command.VendorId, command.Allocations, ct);

        VendorCreditApplicationBody body = new(command.VendorId, command.Date);
        VendorCreditApplication recorded = await payments.RecordCreditApplicationAsync(clientId, body, ct);
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeVendorCreditApplication(recorded.Id, body, command.Allocations, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    public async Task<BillPayment> VoidPaymentAsync(Guid clientId, Guid paymentId, string? reason = null, CancellationToken ct = default)
    {
        BillPayment payment = await payments.GetPaymentAsync(clientId, paymentId, ct)
            ?? throw new InvalidOperationException($"Payment {paymentId} not found.");
        if (payment.Voided)
            throw new InvalidOperationException($"Payment {paymentId} is already voided.");

        // A void reverses the whole payment, including the overpayment that landed as vendor credit. If that
        // credit has since been applied, removing it would drive the credit balance negative (a credit balance
        // on the Vendor Credits asset) — a corrupt state. Refuse; the consuming application must be reversed
        // first. Unapplied is folded from the payment's own entry (the module stores no allocation array to
        // compute it from directly); postedOnly: false gives the immediacy this guard needs — a pending
        // payment being voided must still see its own overpayment.
        BillPaymentPostingAccounts postingForVoid = await accounts.GetPaymentAccountsAsync(clientId, ct);
        decimal allocated = await SettlementRelief.ForSourceAsync(
            ledger, clientId, paymentId, postingForVoid.PayableAccountId, ct, postedOnly: false);
        decimal unapplied = payment.Amount - allocated;
        if (unapplied > 0m)
        {
            decimal creditBalance = await GetVendorCreditBalanceAsync(clientId, payment.VendorId, ct);
            if (creditBalance - unapplied < 0m)
                throw new InvalidOperationException(
                    $"Cannot void payment {paymentId}: its overpayment credit ({unapplied:C}) has already " +
                    $"been applied (available credit is only {creditBalance:C}). Reverse the vendor credit " +
                    $"application(s) first, then void this payment.");
        }

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
        decimal applied = await AppliedToBillAsync(clientId, bill, ct);
        return new BillView(bill, Accounting101.Settlement.Settlement.OpenBalance(bill.Total, applied), Accounting101.Settlement.Settlement.Status(bill.Total, applied));
    }

    /// <summary>A single bill payment plus the bills it was applied to, its unapplied remainder (held as
    /// vendor credit), and its posted journal entry id — for the detail screen. Allocations and the journal
    /// id come from the Posted posting; Unapplied = Amount − Σallocations. Returns null if the payment does
    /// not exist.</summary>
    public async Task<BillPaymentView?> GetBillPaymentViewAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        BillPayment? payment = await payments.GetPaymentAsync(clientId, paymentId, ct);
        if (payment is null) return null;

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, paymentId, ct);
        EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null });

        List<BillAllocationLine> allocations = [];
        if (postingEntry is not null)
        {
            foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                         .Where(l => l.Dimensions.ContainsKey(BillPosting.BillDimension))
                         .GroupBy(l => l.Dimensions[BillPosting.BillDimension]))
            {
                Bill? bill = await bills.GetAsync(clientId, group.Key, ct);
                allocations.Add(new BillAllocationLine(group.Key, bill?.Number, group.Sum(l => l.Amount)));
            }
        }

        decimal unapplied = payment.Amount - allocations.Sum(a => a.Amount);
        return new BillPaymentView(payment, allocations, unapplied, postingEntry?.Id);
    }

    /// <summary>The vendor's bill payments each with its per-bill allocations folded from the GL (Posted-only)
    /// — what the Payments list and the bill-detail "applied payments" section consume. Voided payments are
    /// included (greyed in the UI); a not-yet-Posted payment folds to no allocations.</summary>
    public async Task<IReadOnlyList<BillPaymentWithAllocations>> GetPaymentsWithAllocationsByVendorAsync(
        Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        List<BillPaymentWithAllocations> result = [];
        foreach (BillPayment p in ps)
        {
            IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, p.Id, ct);
            EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null });
            List<Allocation> allocs = [];
            if (postingEntry is not null)
                foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                             .Where(l => l.Dimensions.ContainsKey(BillPosting.BillDimension))
                             .GroupBy(l => l.Dimensions[BillPosting.BillDimension]))
                    allocs.Add(new Allocation(group.Key, group.Sum(l => l.Amount)));
            result.Add(new BillPaymentWithAllocations(p.Id, p.VendorId, p.Date, p.Amount, p.Method, p.Voided, allocs));
        }
        return result;
    }

    /// <summary>Unapplied vendor credit, folded from the ledger's Vendor-axis Vendor Credits subledger.
    /// Vendor Credits is an ASSET (debit-normal); the fold reads available credit directly POSITIVE — NO
    /// negation (the mirror of AR's Customer Credits, a liability, which negates).</summary>
    public async Task<decimal> GetVendorCreditBalanceAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        return (await ledger.GetSubledgerAsync(clientId, posting.VendorCreditsAccountId, "Vendor", null, ct))
            .Where(l => l.DimensionValue == vendorId).Sum(l => l.Balance);
    }

    public async Task<IReadOnlyList<BillView>> ListBillViewsAsync(Guid clientId, Guid vendorId, SettlementFilter? filter, CancellationToken ct = default)
    {
        IReadOnlyList<Bill> vendorBills = await bills.GetByVendorAsync(clientId, vendorId, ct);

        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> apByBill =
            await ledger.GetSubledgerAsync(clientId, posting.PayableAccountId, "Bill", null, ct);
        Dictionary<Guid, decimal> openByBill = apByBill.ToDictionary(l => l.DimensionValue, l => -l.Balance);
        Dictionary<Guid, decimal> applied = vendorBills.ToDictionary(
            b => b.Id, b => b.Total - openByBill.GetValueOrDefault(b.Id, b.Total));

        IEnumerable<BillView> views = vendorBills
            .Where(bill => bill.Status == BillStatus.Entered)
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

    /// <summary>Each allocation must target a live bill of this vendor and not exceed its current open balance.</summary>
    private async Task ValidateAllocationsAsync(Guid clientId, Guid vendorId, IReadOnlyList<Allocation> allocations, CancellationToken ct)
    {
        foreach (Allocation a in allocations)
        {
            Bill bill = await bills.GetAsync(clientId, a.TargetId, ct)
                ?? throw new InvalidOperationException($"Bill {a.TargetId} does not exist.");
            if (bill.Status != BillStatus.Entered)
                throw new InvalidOperationException($"Bill {a.TargetId} is {bill.Status} — only entered bills can be paid.");
            if (bill.VendorId != vendorId)
                throw new InvalidOperationException($"Bill {a.TargetId} belongs to a different vendor.");

            decimal alreadyApplied = await ReservedAgainstBillAsync(clientId, bill, ct);
            if (alreadyApplied + a.Amount > bill.Total)
                throw new InvalidOperationException($"Allocation to bill {a.TargetId} exceeds its open balance.");
        }
    }

    /// <summary>
    /// Total amount applied to one bill — the READ path. Folded Posted-only from the ledger's Bill-axis A/P
    /// subledger (reads reflect only what is actually on the books): applied = bill.Total − open, where
    /// open = −fold (A/P is credit-normal — the debit-positive fold reads the outstanding payable
    /// NEGATIVE). When the bill carries no on-the-books A/P line yet (its own enter entry is still
    /// PendingApproval), <c>open</c> defaults to <c>bill.Total</c> — i.e. applied = 0 — so a freshly
    /// entered but unapproved bill reads as fully open (not Paid). This mirrors how
    /// <see cref="ListBillViewsAsync"/> already defaults an absent fold entry to Total.
    /// </summary>
    private async Task<decimal> AppliedToBillAsync(Guid clientId, Bill bill, CancellationToken ct)
    {
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> fold =
            await ledger.GetSubledgerAsync(clientId, posting.PayableAccountId, "Bill", null, ct);
        decimal open = -(fold.FirstOrDefault(l => l.DimensionValue == bill.Id)?.Balance ?? -bill.Total);
        return bill.Total - open;
    }

    /// <summary>
    /// Total amount reserved against one bill — the WRITE-PATH validation computation used by
    /// <see cref="ValidateAllocationsAsync"/>. Folded PENDING-INCLUSIVE (Posted + PendingApproval,
    /// non-void) so an unapproved relief (payment allocation, credit application) already recorded against
    /// the bill reserves against it: a second, also-unapproved relief then fails this check instead of both
    /// passing and over-relieving the bill once approved. Deliberately NOT used by any read path — reads
    /// must stay Posted-only (see <see cref="AppliedToBillAsync"/>).
    /// </summary>
    private async Task<decimal> ReservedAgainstBillAsync(Guid clientId, Bill bill, CancellationToken ct)
    {
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> fold = await ledger.GetSubledgerAsync(
            clientId, posting.PayableAccountId, "Bill", null, ct, includePending: true);
        decimal open = -(fold.FirstOrDefault(l => l.DimensionValue == bill.Id)?.Balance ?? -bill.Total);
        return bill.Total - open;
    }
}
