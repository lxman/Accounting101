using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Contracts.Tests;

public sealed class Iso4217CurrenciesTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("GBP")]
    [InlineData("JPY")]
    [InlineData("CAD")]
    [InlineData("XAF")] // real circulating supranational currency (CFA Franc BEAC), not a non-currency X-code
    [InlineData("XCD")] // real circulating supranational currency (East Caribbean Dollar)
    [InlineData("XOF")] // real circulating supranational currency (CFA Franc BCEAO)
    [InlineData("XPF")] // real circulating supranational currency (CFP Franc)
    [InlineData("BTN")] // Bhutanese Ngultrum
    [InlineData("SHP")] // Saint Helena Pound
    public void Known_real_currencies_are_recognized(string code) =>
        Assert.True(Iso4217Currencies.IsKnown(code));

    [Theory]
    [InlineData("usd")]   // case-insensitive
    [InlineData("  eur ")] // trimmed
    public void Recognition_is_case_insensitive_and_trimmed(string code) =>
        Assert.True(Iso4217Currencies.IsKnown(code));

    [Theory]
    [InlineData("XXX")]   // no-currency
    [InlineData("XTS")]   // testing
    [InlineData("XAU")]   // gold
    [InlineData("ZZZ")]   // junk
    [InlineData("US")]    // too short
    [InlineData("")]
    [InlineData(null)]
    public void X_codes_and_junk_are_rejected(string? code) =>
        Assert.False(Iso4217Currencies.IsKnown(code));

    [Fact]
    public void Every_row_has_a_nonempty_symbol_and_name()
    {
        Assert.NotEmpty(Iso4217Currencies.All);
        Assert.All(Iso4217Currencies.All, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Code));
            Assert.False(string.IsNullOrWhiteSpace(c.Symbol));
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });
    }

    [Fact]
    public void Find_returns_symbol_for_known_and_null_for_unknown()
    {
        Assert.Equal("€", Iso4217Currencies.Find("EUR")!.Symbol);
        Assert.Null(Iso4217Currencies.Find("XXX"));
    }
}
