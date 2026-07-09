namespace Accounting101.Inventory.Tests;

public sealed class ItemValidationTests
{
    [Fact]
    public void Blank_sku_is_rejected() =>
        Assert.NotNull(ItemValidation.Validate(new ItemBody("  ", "Widget", null, "each")));

    [Fact]
    public void Blank_name_is_rejected() =>
        Assert.NotNull(ItemValidation.Validate(new ItemBody("SKU1", " ", null, "each")));

    [Fact]
    public void Blank_uom_is_rejected() =>
        Assert.NotNull(ItemValidation.Validate(new ItemBody("SKU1", "Widget", null, "")));

    [Fact]
    public void Valid_body_passes() =>
        Assert.Null(ItemValidation.Validate(new ItemBody("SKU1", "Widget", "desc", "each")));
}
