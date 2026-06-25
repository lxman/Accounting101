using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

/// <summary>
/// Verifies that <see cref="LedgerService.OpenAsync"/> seeds an inception freeze at
/// <c>asOf − 1</c>, so any post dated before the client's opening date is rejected by
/// the same closed-period guard that backs ordinary period closes.
/// </summary>
public sealed class OnboardingTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static AuditStamp Stamp() => new()
    {
        CreatedBy = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static Actor User() => new()
    {
        UserId = Guid.NewGuid(),
        Name = "tester",
        Claims = [new Claim("role", "bookkeeper")],
    };

    // -- harness (mirrors LedgerServiceTests) -----------------------------------

    private Guid ClientId { get; } = Guid.NewGuid();
    private Guid AccountA { get; } = Guid.NewGuid();
    private Guid AccountB { get; } = Guid.NewGuid();

    private (LedgerService Service, MongoCheckpointStore Checkpoints) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        LedgerService service = new(fixture.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(fixture.Database));
        return (service, checkpoints);
    }

    /// <summary>
    /// Balanced two-line opening entry (Debit A / Credit B). Used to seed
    /// <see cref="LedgerService.OpenAsync"/>; the accounts are arbitrary because we
    /// are testing the freeze behaviour, not balance assertions.
    /// </summary>
    private IReadOnlyList<Line> OpeningLines() =>
    [
        new Line { Id = Guid.NewGuid(), AccountId = AccountA, Direction = Direction.Debit, Amount = 1000m },
        new Line { Id = Guid.NewGuid(), AccountId = AccountB, Direction = Direction.Credit, Amount = 1000m },
    ];

    /// <summary>
    /// Minimal balanced two-line standard entry dated on <paramref name="date"/>.
    /// </summary>
    private JournalEntry EntryDated(DateOnly date) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: ClientId,
            sequenceNumber: 0,
            effectiveDate: date,
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = AccountA, Direction = Direction.Debit, Amount = 50m },
                new Line { Id = Guid.NewGuid(), AccountId = AccountB, Direction = Direction.Credit, Amount = 50m },
            ]);

    // -- tests ------------------------------------------------------------------

    [Fact]
    public async Task Onboarding_seeds_the_inception_freeze_at_the_day_before_opening()
    {
        (LedgerService service, MongoCheckpointStore checkpoints) = NewLedger();
        DateOnly opening = new(2024, 1, 1);
        await service.OpenAsync(ClientId, opening, OpeningLines(), User());
        Assert.Equal(new DateOnly(2023, 12, 31), await checkpoints.GetClosedThroughAsync(ClientId));
    }

    [Fact]
    public async Task Post_dated_before_opening_is_rejected()
    {
        (LedgerService service, _) = NewLedger();
        DateOnly opening = new(2024, 1, 1);
        await service.OpenAsync(ClientId, opening, OpeningLines(), User());

        // The classic fat-finger footgun: default DateOnly / 0001-01-01
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PostAsync(EntryDated(new DateOnly(1, 1, 1)), User()));
        Assert.Contains("closed", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Also reject the day before opening
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PostAsync(EntryDated(new DateOnly(2023, 12, 31)), User()));
    }

    [Fact]
    public async Task Post_on_or_after_opening_is_allowed()
    {
        (LedgerService service, _) = NewLedger();
        DateOnly opening = new(2024, 1, 1);
        await service.OpenAsync(ClientId, opening, OpeningLines(), User());

        // On the opening date — allowed
        await service.PostAsync(EntryDated(new DateOnly(2024, 1, 1)), User());
        // After the opening date — allowed
        await service.PostAsync(EntryDated(new DateOnly(2024, 1, 15)), User());
    }

    [Fact]
    public async Task Opening_entry_is_on_the_books_after_onboarding()
    {
        (LedgerService service, _) = NewLedger();
        DateOnly opening = new(2024, 1, 1);
        JournalEntry openingEntry = await service.OpenAsync(ClientId, opening, OpeningLines(), User());
        JournalEntry? read = await service.GetEntryAsync(ClientId, openingEntry.Id);
        Assert.NotNull(read);
        // Posted+approved, not blocked by its own inception freeze
        Assert.Equal(PostingState.Posted, read!.Posting);
    }

    [Fact]
    public async Task First_real_close_still_works_after_inception_seed()
    {
        (LedgerService service, MongoCheckpointStore checkpoints) = NewLedger();
        DateOnly opening = new(2024, 1, 1);
        await service.OpenAsync(ClientId, opening, OpeningLines(), User());

        // Post and approve an in-period entry so the close gate isn't blocked
        JournalEntry inPeriod = EntryDated(new DateOnly(2024, 1, 15));
        await service.PostAsync(inPeriod, User());
        await service.ApproveAsync(inPeriod.Id, User());

        // Must NOT be "already closed" — the inception freeze is at 2023-12-31, close through 2024-01-31 is later
        await service.CloseAsync(ClientId, new DateOnly(2024, 1, 31), User());
        Assert.Equal(new DateOnly(2024, 1, 31), await checkpoints.GetClosedThroughAsync(ClientId));
    }

    [Fact]
    public async Task Opening_balances_remain_empty_after_onboarding()
    {
        (LedgerService service, MongoCheckpointStore checkpoints) = NewLedger();
        DateOnly opening = new(2024, 1, 1);
        await service.OpenAsync(ClientId, opening, OpeningLines(), User());
        Assert.Empty(await checkpoints.GetOpeningBalancesAsync(ClientId));
    }
}
