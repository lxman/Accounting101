using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;

namespace Accounting101.Payables.Tests;

public sealed class BillDraftLifecycleTests
{
    private sealed record Harness(BillService Bills, InMemoryBillStore Store, FakeLedgerClient Ledger);

    private static async Task<(Harness h, Guid clientId, Guid vendorId)> MakeAsync()
    {
        InMemoryVendorStore vendors = new();
        InMemoryBillStore bills = new();
        FakeLedgerClient ledger = new();
        FixedBillAccountsProvider accounts = new(
            new BillPostingAccounts { PayableAccountId = Guid.NewGuid() },
            new BillPaymentPostingAccounts { PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid() });
        BillService service = new(bills, vendors, accounts, ledger);
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        await vendors.SaveAsync(clientId, new Vendor { Id = vendorId, Name = "Acme" });
        return (new Harness(service, bills, ledger), clientId, vendorId);
    }

    private static BillBody Body(Guid vendorId) => new(
        vendorId, new DateOnly(2026, 3, 1), null, "VENDOR-REF", null,
        [new BillLineBody("Rent", 100m, Guid.NewGuid())]);

    [Fact]
    public async Task Draft_is_editable_and_keeps_it_a_draft()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        BillBody edited = Body(vendorId) with { Memo = "updated" };
        Bill updated = await h.Bills.EditDraftAsync(clientId, draft.Id, edited);

        Assert.Equal(BillStatus.Draft, updated.Status);
        Assert.Null(updated.Number);
        Assert.Equal("updated", (await h.Store.GetAsync(clientId, draft.Id))!.Memo);
    }

    [Fact]
    public async Task Draft_is_discardable_and_leaves_no_trace()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        await h.Bills.DiscardDraftAsync(clientId, draft.Id);

        Assert.Null(await h.Store.GetAsync(clientId, draft.Id));
    }

    [Fact]
    public async Task Discard_only_works_on_a_draft()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bills.DiscardDraftAsync(clientId, entered.Id));
    }

    [Fact]
    public async Task Enter_creates_a_new_id_deletes_the_draft_and_assigns_a_number()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        Assert.NotEqual(draft.Id, entered.Id);
        Assert.Equal(BillStatus.Entered, entered.Status);
        Assert.NotNull(entered.Number);
        Assert.Null(await h.Store.GetAsync(clientId, draft.Id));              // draft gone
        Assert.NotNull(await h.Store.GetAsync(clientId, entered.Id));         // entered present
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(entered.Id, entry.SourceRef);                            // posted under the ENTERED id
    }

    [Fact]
    public async Task Enter_only_works_on_a_draft()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bills.EnterAsync(clientId, entered.Id));
    }

    [Fact]
    public async Task Void_keeps_the_entered_id_and_marks_void()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);
        // The fake ledger posts PendingApproval; void withdraws the pending entry, then voids the doc.
        Bill voided = await h.Bills.VoidAsync(clientId, entered.Id);

        Assert.Equal(entered.Id, voided.Id);
        Assert.Equal(BillStatus.Void, voided.Status);
    }

    [Fact]
    public async Task Enter_that_fails_preflight_leaves_the_bill_a_draft_and_posts_nothing()
    {
        // Same harness shape as the other tests, but we need the fake ledger's OnValidate hook.
        InMemoryVendorStore vendors = new();
        InMemoryBillStore bills = new();
        FakeLedgerClient ledger = new();
        ledger.OnValidate = _ => throw new LedgerClientException(409, "period closed");
        // EnterAsync only consults the bill accounts; the payment accounts is required by the provider ctor but unused here.
        BillService service = new(bills, vendors,
            new FixedBillAccountsProvider(
                new BillPostingAccounts { PayableAccountId = Guid.NewGuid() },
                new BillPaymentPostingAccounts { PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid() }),
            ledger);
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        await vendors.SaveAsync(clientId, new Vendor { Id = vendorId, Name = "Acme" });

        Bill draft = await service.DraftAsync(clientId, Body(vendorId));

        await Assert.ThrowsAsync<LedgerClientException>(() => service.EnterAsync(clientId, draft.Id));
        Assert.Empty(ledger.Posted);
        Bill? stillDraft = await bills.GetAsync(clientId, draft.Id);
        Assert.NotNull(stillDraft);
        Assert.Equal(BillStatus.Draft, stillDraft!.Status);   // NOT entered — no orphan
    }
}
