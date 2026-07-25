namespace Accounting101.Payables.Tests;

/// <summary>The duplicate lookup behind the e-invoicing import warning. The case that matters most is
/// the draft: the screen's previous source (the bill list) filters to Entered, so importing the same
/// invoice twice before entering the first produced no warning at all.</summary>
public sealed class BillDuplicateTests
{
    private static BillBody Body(Guid vendorId, string? reference) =>
        new(vendorId, new DateOnly(2026, 3, 1), null, reference, null,
            [new BillLineBody("Rent", 100m, Guid.NewGuid())]);

    [Fact]
    public async Task A_draft_with_a_matching_reference_is_returned()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Equal(draft.Id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task An_entered_bill_with_a_matching_reference_is_returned()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Equal(entered.Id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task A_voided_bill_is_not_a_duplicate()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);
        await h.Bills.VoidAsync(clientId, entered.Id, "duplicate");

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Matching_ignores_case_and_surrounding_whitespace_on_both_sides()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, "  inv-9 "));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, " INV-9  ");

        Assert.Single(hits);
    }

    [Fact]
    public async Task A_bill_with_no_vendor_reference_never_matches()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, null));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task A_blank_reference_matches_nothing()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));

        Assert.Empty(await h.Bills.FindDuplicatesAsync(clientId, vendorId, "   "));
    }

    [Fact]
    public async Task No_matches_returns_an_empty_list()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-1"));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.NotNull(hits);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Another_vendors_bill_with_the_same_reference_is_not_a_duplicate()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Guid otherVendorId = Guid.NewGuid();
        await h.Vendors.SaveAsync(clientId, new Vendor { Id = otherVendorId, Name = "OtherCo" });
        await h.Bills.DraftAsync(clientId, Body(otherVendorId, "INV-9"));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Empty(hits);
    }
}
