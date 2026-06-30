using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

public sealed class BillServiceTests
{
    private static readonly BillPostingAccounts BillAccounts = new() { PayableAccountId = Guid.NewGuid() };
    private static readonly BillPaymentPostingAccounts PayAccounts = new()
    {
        PayableAccountId = BillAccounts.PayableAccountId, CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
    };

    internal sealed record Harness(BillService Bills, FakeLedgerClient Ledger, InMemoryBillStore BillStore, InMemoryVendorStore Vendors);

    internal static async Task<(Harness h, Guid clientId, Guid vendorId)> SetupAsync()
    {
        Guid clientId = Guid.NewGuid(), vendorId = Guid.NewGuid();
        InMemoryVendorStore vendors = new();
        await vendors.SaveAsync(clientId, new Vendor { Id = vendorId, Name = "PropCo" });
        InMemoryBillStore billStore = new();
        FakeLedgerClient ledger = new();
        BillService bills = new(billStore, vendors, new FixedBillAccountsProvider(BillAccounts, PayAccounts), ledger);
        return (new Harness(bills, ledger, billStore, vendors), clientId, vendorId);
    }

    [Fact]
    public async Task Entering_a_bill_posts_a_pending_ap_entry()
    {
        (Harness h, Guid clientId, Guid vendorId) = await SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 6000m, Guid.NewGuid())]));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        Assert.NotEqual(draft.Id, entered.Id);
        Assert.Equal(BillStatus.Entered, entered.Status);
        Assert.StartsWith("BILL-", entered.Number);
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal("Bill", entry.SourceType);
        Assert.Equal(entered.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Edit_draft_replaces_body_and_keeps_it_a_draft()
    {
        (Harness h, Guid clientId, Guid vendorId) = await SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 6000m, Guid.NewGuid())]));

        Bill updated = await h.Bills.EditDraftAsync(clientId, draft.Id, new BillBody(vendorId, new DateOnly(2026, 3, 1), null, null, "edited",
            [new BillLineBody("Rent", 6000m, Guid.NewGuid())]));

        Assert.Equal(BillStatus.Draft, updated.Status);
        Assert.Equal("edited", (await h.BillStore.GetAsync(clientId, draft.Id))!.Memo);
    }

    [Fact]
    public async Task Discard_draft_removes_it()
    {
        (Harness h, Guid clientId, Guid vendorId) = await SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 6000m, Guid.NewGuid())]));

        await h.Bills.DiscardDraftAsync(clientId, draft.Id);

        Assert.Null(await h.BillStore.GetAsync(clientId, draft.Id));
    }

    [Fact]
    public async Task Drafting_a_bill_for_an_unknown_vendor_is_rejected()
    {
        (Harness h, Guid clientId, _) = await SetupAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bills.DraftAsync(clientId,
            new BillBody(Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null, null, [new BillLineBody("X", 1m, Guid.NewGuid())])));
    }
}

internal sealed class FixedBillAccountsProvider(BillPostingAccounts bill, BillPaymentPostingAccounts pay) : IBillAccountsProvider
{
    public Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(bill);
    public Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(pay);
}
