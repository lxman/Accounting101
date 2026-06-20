using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Mongo.Tests;

/// <summary>
/// The module's persistence round-trips a customer and an invoice (lines, dates, tax, status intact),
/// and the invoice-number counter hands out a gapless sequence per client.
/// </summary>
public sealed class MongoStoreTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private FixedDatabaseResolver Resolver() => new(fixture.Database);

    [Fact]
    public async Task Customer_round_trips()
    {
        MongoCustomerStore store = new(Resolver());
        var client = Guid.NewGuid();
        Customer customer = new() { Id = Guid.NewGuid(), Name = "Acme", Email = "ap@acme.test" };

        await store.SaveAsync(client, customer);
        Customer? read = await store.GetAsync(client, customer.Id);

        Assert.NotNull(read);
        Assert.Equal("Acme", read!.Name);
        Assert.Equal("ap@acme.test", read.Email);
    }

    [Fact]
    public async Task Invoice_round_trips_with_lines_dates_and_derived_totals()
    {
        MongoInvoiceStore store = new(Resolver());
        var client = Guid.NewGuid();
        Invoice invoice = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Number = "INV-00007",
            IssueDate = new DateOnly(2026, 3, 31),
            DueDate = new DateOnly(2026, 4, 30),
            Status = InvoiceStatus.Issued,
            TaxRate = 0.07m,
            Memo = "Q1 work",
            Lines =
            [
                new InvoiceLine { Description = "Widgets", Quantity = 2m, UnitPrice = 50m },
                new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 30m, Taxable = false },
            ],
        };

        await store.SaveAsync(client, invoice);
        Invoice? read = await store.GetAsync(client, invoice.Id);

        Assert.NotNull(read);
        Assert.Equal("INV-00007", read!.Number);
        Assert.Equal(new DateOnly(2026, 3, 31), read.IssueDate);
        Assert.Equal(new DateOnly(2026, 4, 30), read.DueDate);
        Assert.Equal(InvoiceStatus.Issued, read.Status);
        Assert.Equal(2, read.Lines.Count);
        Assert.Equal(130m, read.Subtotal);     // derived survives the round-trip
        Assert.Equal(7m, read.Tax);
        Assert.Equal(137m, read.Total);
        Assert.False(read.Lines.Single(l => l.Description == "Consulting").Taxable);
    }

    [Fact]
    public async Task Invoice_numbers_are_a_gapless_per_client_sequence()
    {
        MongoInvoiceNumbers numbers = new(Resolver());
        var client = Guid.NewGuid();

        Assert.Equal("INV-00001", await numbers.NextAsync(client));
        Assert.Equal("INV-00002", await numbers.NextAsync(client));
        Assert.Equal("INV-00003", await numbers.NextAsync(client));
    }
}
