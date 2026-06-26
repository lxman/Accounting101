namespace Accounting101.Ledger.Contracts.Tests;

public class EntryIdentityTests
{
    static readonly Guid G1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid G2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact] public void Deterministic()        => Assert.Equal(EntryIdentity.ForSource("Invoice", G1), EntryIdentity.ForSource("Invoice", G1));
    [Fact] public void Distinct_on_type()     => Assert.NotEqual(EntryIdentity.ForSource("Invoice", G1), EntryIdentity.ForSource("Bill", G1));
    [Fact] public void Distinct_on_ref()      => Assert.NotEqual(EntryIdentity.ForSource("Invoice", G1), EntryIdentity.ForSource("Invoice", G2));

    [Fact]
    public void Is_rfc4122_version5()
    {
        // After Guid round-trip the version/variant live at specific positions; assert via the canonical string instead:
        string s = EntryIdentity.ForSource("Invoice", G1).ToString();
        Assert.Equal('5', s[14]);                 // version nibble
        Assert.Contains(s[19], "89ab");           // RFC4122 variant nibble
    }

    [Fact]
    public void Known_vector_pins_the_algorithm()
    {
        // Compute ONCE with the final implementation, then hardcode here so the algorithm cannot drift.
        // Pinned from first green run — algorithm must not drift.
        Assert.Equal(Guid.Parse("ae2bbf10-b976-5737-adb7-f0e8c4007b8e"), EntryIdentity.ForSource("Invoice", G1));
    }

    [Fact] public void Null_or_empty_type_throws() => Assert.ThrowsAny<ArgumentException>(() => EntryIdentity.ForSource("", G1));
}
