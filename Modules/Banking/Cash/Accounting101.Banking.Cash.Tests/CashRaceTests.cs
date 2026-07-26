using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THESE VALID: revert the service's <c>replayed = !result.Created;</c> to
/// <c>replayed = known is not null;</c> and both racers claim a create, so the single-create
/// assertion sees two and fails. A concurrency test without that property is worse than none —
/// it advertises protection it does not provide.
/// </summary>
public sealed class CashRaceTests
{
    private static readonly CashPostingAccounts Accounts = new()
    {
        CashAccountId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Two_disbursement_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryCashDisbursementStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousCashDisbursementStore racingStore = new(inner, rendezvous);
        InMemoryCashDepositStore depositStore = new();
        FakeLedgerClient ledger = new();
        CashService service = new(racingStore, depositStore, new FixedCashAccountsProvider(Accounts), ledger);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        Guid interestAccount = Guid.NewGuid();
        Guid loanPayableAccount = Guid.NewGuid();
        CashDisbursementBody body = new(
            Lines: [new CashLine(interestAccount, 500m), new CashLine(loanPayableAccount, 1_500m)],
            Date: new DateOnly(2026, 6, 30),
            Reference: "LOAN-2026-06",
            Memo: "Loan payment — interest + principal");

        Task<(CashDisbursement Disbursement, bool Replayed)> first =
            service.RecordDisbursementAsync(clientId, body, retryKey);
        Task<(CashDisbursement Disbursement, bool Replayed)> second =
            service.RecordDisbursementAsync(clientId, body, retryKey);
        (CashDisbursement Disbursement, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Disbursement.Id);
        Assert.Equal(retryKey, results[1].Disbursement.Id);
    }

    [Fact]
    public async Task Two_deposit_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryCashDepositStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousCashDepositStore racingStore = new(inner, rendezvous);
        InMemoryCashDisbursementStore disbursementStore = new();
        FakeLedgerClient ledger = new();
        CashService service = new(disbursementStore, racingStore, new FixedCashAccountsProvider(Accounts), ledger);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        Guid membersCapitalAccount = Guid.NewGuid();
        CashDepositBody body = new(
            Lines: [new CashLine(membersCapitalAccount, 25_000m)],
            Date: new DateOnly(2026, 1, 2),
            Reference: "CONTRIB-2026",
            Memo: "Owner contribution");

        Task<(CashDeposit Deposit, bool Replayed)> first = service.RecordDepositAsync(clientId, body, retryKey);
        Task<(CashDeposit Deposit, bool Replayed)> second = service.RecordDepositAsync(clientId, body, retryKey);
        (CashDeposit Deposit, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Deposit.Id);
        Assert.Equal(retryKey, results[1].Deposit.Id);
    }
}
