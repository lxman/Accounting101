using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Accounting101.Ledger.Mongo.Tests;

/// <summary>
/// Regression coverage for the "stale field on a legacy document" defect caught by the dev-stack
/// smoke test (never reproduced by the EphemeralMongo suite, which always starts empty).
///
/// Task 8 of the AR-ledger-first work dropped the stored <c>Allocations</c> array from the
/// Receivables settlement bodies (<c>PaymentBody</c>, <c>WriteOffBody</c>, <c>CreditNoteBody</c>,
/// <c>CreditApplicationBody</c> — see Modules/Receivables/Accounting101.Receivables). Those bodies
/// are serialized generically by <c>ScopedDocumentStore</c> via <c>BsonSerializer.Deserialize&lt;T&gt;</c>,
/// and the domain assembly has no MongoDB dependency, so no per-type <c>[BsonIgnoreExtraElements]</c>
/// is possible. A document written before the field removal still carries the stale
/// <c>Allocations</c> element on disk, which — absent the fix — throws
/// <see cref="FormatException"/> on read ("Element 'Allocations' does not match any field or
/// property of class PaymentBody"), a 500 on any list/get of payments or dispositions.
///
/// The fix is <see cref="LedgerMongoBootstrap.RegisterOnce"/> registering a global
/// <c>IgnoreExtraElementsConvention(true)</c>. This project has no reference to the Receivables
/// module, so <see cref="LegacyBodyProbe"/> stands in for <c>PaymentBody</c> — same shape
/// (Guid/DateOnly/decimal/string? fields), and the convention is global, so it proves the exact
/// same behavior the real body types get.
///
/// RED verification note: because <see cref="LedgerMongoBootstrap.RegisterOnce"/> is
/// process-once (an <c>Interlocked.Exchange</c> latch) and a registered MongoDB convention cannot
/// be unregistered within a running process, this test cannot toggle RED/GREEN in a single test
/// run. It WAS confirmed RED out-of-process: temporarily commenting out the
/// <c>ConventionRegistry.Register(...)</c> call in LedgerMongoBootstrap.cs and running this test
/// alone (`dotnet test --filter LegacyDocumentSerializationTests`) in a fresh process reproduces
/// the exact <see cref="FormatException"/> described above; restoring the call turns it green
/// again. That block must not be removed — see LedgerMongoBootstrap.cs.
/// </summary>
public sealed class LegacyDocumentSerializationTests
{
    /// <summary>Mirrors the CURRENT (trimmed) shape of Accounting101.Receivables.PaymentBody:
    /// <c>record PaymentBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Method)</c>.</summary>
    private sealed record LegacyBodyProbe(Guid CustomerId, DateOnly Date, decimal Amount, string? Method);

    /// <summary>Mirrors the PRE-Task-8 shape of the same body, which still carried the per-invoice
    /// allocation array that has since moved onto ledger dimensions.</summary>
    private sealed record LegacyBodyProbeWithAllocations(
        Guid CustomerId, DateOnly Date, decimal Amount, string? Method, List<string> Allocations);

    [Fact]
    public void Deserializing_a_pre_removal_document_with_a_stale_field_does_not_throw_and_maps_real_fields()
    {
        // Ensure the bootstrap convention is registered (it's also called from the Mongo store's
        // static constructor, but this test has no dependency that would trigger that — call it
        // explicitly so the assertion doesn't depend on load order).
        LedgerMongoBootstrap.RegisterOnce();

        Guid customerId = Guid.NewGuid();
        DateOnly date = new(2026, 6, 15);
        LegacyBodyProbeWithAllocations legacy = new(
            customerId, date, 1234.56m, "ACH", Allocations: ["invoice-1", "invoice-2"]);

        // Serialize exactly the way ScopedDocumentStore.BuildDoc does: body!.ToBsonDocument().
        // This is what a document written BEFORE the field removal looks like on disk today —
        // it still carries the stale "Allocations" element.
        BsonDocument onDisk = legacy.ToBsonDocument();
        Assert.True(onDisk.Contains("Allocations"), "fixture sanity: the stale element must be present on the simulated legacy document");

        // The read path (ScopedDocumentStore.GetAsync/QueryAsync) deserializes into the CURRENT,
        // trimmed type — which has no Allocations property at all.
        LegacyBodyProbe read = BsonSerializer.Deserialize<LegacyBodyProbe>(onDisk);

        Assert.Equal(customerId, read.CustomerId);
        Assert.Equal(date, read.Date);
        Assert.Equal(1234.56m, read.Amount);
        Assert.Equal("ACH", read.Method);
    }

    [Fact]
    public void Deserializing_a_well_formed_current_document_still_round_trips_cleanly()
    {
        // The global convention must be behavior-preserving for ordinary, up-to-date documents —
        // no regressions for the common case.
        LedgerMongoBootstrap.RegisterOnce();

        Guid customerId = Guid.NewGuid();
        DateOnly date = new(2026, 7, 1);
        LegacyBodyProbe current = new(customerId, date, 42.00m, null);

        BsonDocument onDisk = current.ToBsonDocument();
        LegacyBodyProbe read = BsonSerializer.Deserialize<LegacyBodyProbe>(onDisk);

        Assert.Equal(customerId, read.CustomerId);
        Assert.Equal(date, read.Date);
        Assert.Equal(42.00m, read.Amount);
        Assert.Null(read.Method);
    }
}
