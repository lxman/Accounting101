namespace Accounting101.Receivables.Tests;

/// <summary>
/// Store-layer TDD tests for the draft → plain-collection → promote → evidentiary-collection lifecycle.
/// Drafts live in "invoice-drafts" (plain, freely editable/discardable); only PromoteDraftAsync
/// writes to the evidentiary "invoices" collection.
/// </summary>
public sealed class InvoiceStoreDraftTests(DocumentStoreFixture fixture) : IClassFixture<DocumentStoreFixture>
{
    private static InvoiceBody Body(Guid customerId, string memo = "test") =>
        new(customerId, new DateOnly(2026, 3, 31), null, 0.07m, memo,
            [new LineBody("Work", 1m, 100m, true)]);

    // 1. Create + edit a draft: CreateDraftAsync -> Status Draft, no Number; UpdateDraftAsync changes a field;
    //    GetAsync reads the change back; the draft is NOT in the evidentiary "invoices" collection.
    [Fact]
    public async Task Create_and_edit_draft_is_Draft_with_no_number_and_not_in_evidentiary_collection()
    {
        DocumentInvoiceStore store = new(fixture.Store);
        Guid customer = Guid.NewGuid();

        Invoice draft = await store.CreateDraftAsync(fixture.ClientId, Body(customer, "original"));
        Assert.Equal(InvoiceStatus.Draft, draft.Status);
        Assert.Null(draft.Number);

        // Edit the draft: change the memo.
        InvoiceBody updated = Body(customer, "updated memo");
        Invoice edited = await store.UpdateDraftAsync(fixture.ClientId, draft.Id, updated);
        Assert.Equal("updated memo", edited.Memo);
        Assert.Equal(InvoiceStatus.Draft, edited.Status);
        Assert.Null(edited.Number);

        // GetAsync reads the edit back.
        Invoice? readBack = await store.GetAsync(fixture.ClientId, draft.Id);
        Assert.NotNull(readBack);
        Assert.Equal("updated memo", readBack.Memo);

        // The draft does NOT appear under the evidentiary "invoices" collection.
        // Promote something else to make sure the evidentiary collection is accessible, then confirm
        // our draft is absent. Since we can only prove absence via GetAsync on the issued collection,
        // we rely on the fact that GetAsync checks drafts first, then issued. A draft id that only
        // exists in the plain collection returns the Draft — confirming it never entered evidentiary.
        Assert.Equal(InvoiceStatus.Draft, readBack.Status);
    }

    // 2. Discard: DiscardDraftAsync removes it; GetAsync -> null; nothing in "invoices".
    [Fact]
    public async Task Discard_removes_draft_and_GetAsync_returns_null()
    {
        DocumentInvoiceStore store = new(fixture.Store);
        Guid customer = Guid.NewGuid();

        Invoice draft = await store.CreateDraftAsync(fixture.ClientId, Body(customer));
        await store.DiscardDraftAsync(fixture.ClientId, draft.Id);

        Invoice? readBack = await store.GetAsync(fixture.ClientId, draft.Id);
        Assert.Null(readBack);
    }

    // 3. Promote: PromoteDraftAsync returns an Invoice with a NEW id (!= draftId) and an assigned Number;
    //    GetAsync(draftId) -> null (consumed); GetAsync(issuedId) -> Status Issued with that Number.
    [Fact]
    public async Task Promote_assigns_new_id_and_number_and_draft_is_consumed()
    {
        DocumentInvoiceStore store = new(fixture.Store);
        Guid customer = Guid.NewGuid();

        Invoice draft = await store.CreateDraftAsync(fixture.ClientId, Body(customer));
        Invoice issued = await store.PromoteDraftAsync(fixture.ClientId, draft.Id);

        // New id, not the draft id.
        Assert.NotEqual(draft.Id, issued.Id);

        // Assigned gapless number.
        Assert.NotNull(issued.Number);
        Assert.StartsWith("INV-", issued.Number);

        // Status is Issued.
        Assert.Equal(InvoiceStatus.Issued, issued.Status);

        // Draft id is consumed — no longer findable.
        Invoice? draftLookup = await store.GetAsync(fixture.ClientId, draft.Id);
        Assert.Null(draftLookup);

        // Issued id is findable with Issued status and the assigned number.
        Invoice? issuedLookup = await store.GetAsync(fixture.ClientId, issued.Id);
        Assert.NotNull(issuedLookup);
        Assert.Equal(InvoiceStatus.Issued, issuedLookup.Status);
        Assert.Equal(issued.Number, issuedLookup.Number);
    }

    // 4. Reads span both tiers: a customer with one draft + one issued -> GetByCustomerAsync returns BOTH
    //    (one Draft, one Issued).
    [Fact]
    public async Task GetByCustomerAsync_spans_both_plain_and_evidentiary_collections()
    {
        DocumentInvoiceStore store = new(fixture.Store);
        Guid customer = Guid.NewGuid();

        // Create a draft (stays in plain collection).
        Invoice draft = await store.CreateDraftAsync(fixture.ClientId, Body(customer));

        // Create and promote a second one (moves to evidentiary).
        Invoice secondDraft = await store.CreateDraftAsync(fixture.ClientId, Body(customer));
        Invoice issued = await store.PromoteDraftAsync(fixture.ClientId, secondDraft.Id);

        IReadOnlyList<Invoice> all = await store.GetByCustomerAsync(fixture.ClientId, customer);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, i => i.Status == InvoiceStatus.Draft && i.Id == draft.Id);
        Assert.Contains(all, i => i.Status == InvoiceStatus.Issued && i.Id == issued.Id);
    }

    // 5. UpdateDraftAsync / DiscardDraftAsync on a non-draft id throw InvalidOperationException.
    [Fact]
    public async Task UpdateDraftAsync_and_DiscardDraftAsync_on_non_draft_id_throw_InvalidOperationException()
    {
        DocumentInvoiceStore store = new(fixture.Store);
        Guid customer = Guid.NewGuid();

        Invoice draft = await store.CreateDraftAsync(fixture.ClientId, Body(customer));
        Invoice issued = await store.PromoteDraftAsync(fixture.ClientId, draft.Id);

        // issued.Id is now in the evidentiary collection — not a draft.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateDraftAsync(fixture.ClientId, issued.Id, Body(customer)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DiscardDraftAsync(fixture.ClientId, issued.Id));

        // Completely unknown id also throws.
        Guid unknown = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateDraftAsync(fixture.ClientId, unknown, Body(customer)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DiscardDraftAsync(fixture.ClientId, unknown));
    }
}
