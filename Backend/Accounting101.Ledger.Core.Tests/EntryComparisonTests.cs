using System.Collections.ObjectModel;
using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Tests;

public class EntryComparisonTests
{
    private static readonly Guid Acct1 = Guid.NewGuid();
    private static readonly Guid Acct2 = Guid.NewGuid();
    private static readonly Guid SomeUserId = Guid.NewGuid();
    private static readonly Guid DefaultClientId = Guid.NewGuid();
    private static readonly Guid DefaultSourceRef = Guid.NewGuid();

    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>Balanced debit/credit pair against the two fixed accounts.</summary>
    private static IReadOnlyList<Line> Pair(Guid debitAcct, Guid creditAcct, decimal amount,
        IReadOnlyDictionary<string, Guid>? debitDimensions = null,
        IReadOnlyDictionary<string, Guid>? creditDimensions = null) =>
        [
            new Line
            {
                Id = Guid.NewGuid(),
                AccountId = debitAcct,
                Direction = Direction.Debit,
                Amount = amount,
                Dimensions = debitDimensions ?? ReadOnlyDictionary<string, Guid>.Empty,
            },
            new Line
            {
                Id = Guid.NewGuid(),
                AccountId = creditAcct,
                Direction = Direction.Credit,
                Amount = amount,
                Dimensions = creditDimensions ?? ReadOnlyDictionary<string, Guid>.Empty,
            },
        ];

    private static JournalEntry Build(
        DateOnly? date = null,
        IReadOnlyList<Line>? lines = null,
        Guid? sourceRef = null,
        EntryType type = EntryType.Standard,
        string? reference = null,
        string? memo = null,
        string? sourceType = null) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: DefaultClientId,
            sequenceNumber: 1,
            effectiveDate: date ?? new DateOnly(2024, 6, 30),
            postedAt: DateTimeOffset.UnixEpoch,
            type: type,
            audit: Stamp(),
            lines: lines ?? Pair(Acct1, Acct2, 100m),
            sourceRef: sourceRef ?? DefaultSourceRef,
            reference: reference,
            memo: memo,
            sourceType: sourceType);

    [Fact]
    public void Identical_entries_match()
    {
        JournalEntry a = Build(date: new DateOnly(2024, 6, 30), lines: Pair(Acct1, Acct2, 100m));
        JournalEntry b = Build(date: new DateOnly(2024, 6, 30), lines: Pair(Acct1, Acct2, 100m));
        Assert.True(EntryComparison.SameFinancialContent(a, b));
    }

    [Fact]
    public void Differing_amount_does_not_match()
    {
        Assert.False(EntryComparison.SameFinancialContent(
            Build(lines: Pair(Acct1, Acct2, 100m)),
            Build(lines: Pair(Acct1, Acct2, 101m))));
    }

    [Fact]
    public void Differing_effective_date_does_not_match()
    {
        Assert.False(EntryComparison.SameFinancialContent(
            Build(date: new DateOnly(2024, 6, 30)),
            Build(date: new DateOnly(2024, 7, 1))));
    }

    [Fact]
    public void Differing_source_ref_does_not_match()
    {
        Assert.False(EntryComparison.SameFinancialContent(
            Build(sourceRef: Guid.NewGuid()),
            Build(sourceRef: Guid.NewGuid())));
    }

    [Fact]
    public void Differing_dimensions_do_not_match()
    {
        Guid customer1 = Guid.NewGuid();
        Guid customer2 = Guid.NewGuid();

        IReadOnlyDictionary<string, Guid> dims1 =
            new ReadOnlyDictionary<string, Guid>(new Dictionary<string, Guid> { ["Customer"] = customer1 });
        IReadOnlyDictionary<string, Guid> dims2 =
            new ReadOnlyDictionary<string, Guid>(new Dictionary<string, Guid> { ["Customer"] = customer2 });

        Assert.False(EntryComparison.SameFinancialContent(
            Build(lines: Pair(Acct1, Acct2, 100m, debitDimensions: dims1)),
            Build(lines: Pair(Acct1, Acct2, 100m, debitDimensions: dims2))));
    }

    [Fact]
    public void Reference_and_memo_are_ignored()
    {
        JournalEntry a = Build(lines: Pair(Acct1, Acct2, 100m), reference: "R-1", memo: "first");
        JournalEntry b = Build(lines: Pair(Acct1, Acct2, 100m), reference: "R-2", memo: "second");
        Assert.True(EntryComparison.SameFinancialContent(a, b));
    }

    [Fact]
    public void Lifecycle_state_is_ignored()
    {
        JournalEntry pending = Build(lines: Pair(Acct1, Acct2, 100m));
        JournalEntry posted = pending.Approve(SomeUserId);
        Assert.True(EntryComparison.SameFinancialContent(pending, posted));
    }

    [Fact]
    public void Differing_source_type_does_not_match()
    {
        Guid sharedRef = Guid.NewGuid();
        JournalEntry a = Build(sourceRef: sharedRef, sourceType: "Invoice");
        JournalEntry b = Build(sourceRef: sharedRef, sourceType: "Bill");
        Assert.False(EntryComparison.SameFinancialContent(a, b));
    }
}
