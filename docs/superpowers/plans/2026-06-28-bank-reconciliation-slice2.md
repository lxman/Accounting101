# Bank Reconciliation — Slice 2 (Auto-Match by Amount) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `POST /reconciliations/{id}/auto-match` endpoint that proposes 1:1 pairings of bank statement lines to uncleared eligible ledger entries by exact signed amount (nearest-date tiebreak); `?apply=true` additionally clears the matched entries through Slice 1's `ClearAsync` and returns the updated worksheet.

**Architecture:** A pure `AutoMatcher` static (beside `ReconciliationMath`) holds the matching algorithm. `ReconciliationService` gains `AutoMatchAsync` (read-only proposal) and `AutoMatchApplyAsync` (proposal → `ClearAsync`). One new endpoint with a `bool? apply` query flag. Still read-only on the GL — "apply" only edits the reconciliation's own cleared set.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, xUnit, EphemeralMongo for E2E. Extends the Slice 1 Reconciliation module (`Modules/Banking/Reconciliation/`).

## Global Constraints

- New code only under `Modules/Banking/Reconciliation/`. No change to other modules. Slice 1 behavior must stay unchanged.
- **No GL mutation.** Auto-match uses the existing read-only `IReconciliationLedgerReader`; apply mutates only the `Reconciliation` document via the existing `ClearAsync`.
- Match key: exact signed-amount equality — `BankStatementLine.Amount == MatchableEntry.CashEffect` (same sign convention; deposit `+`, payment `−`). 1:1 assignment. Nearest-date tiebreak (`min |entry.Date − line.Date|`, then by `EntryId`). NO fuzzy/near-amount, NO hard date-window, NO many-to-one.
- Only **uncleared** eligible entries are matched (already-cleared entries are excluded before matching). Eligible = the existing `EligibleEntriesAsync` filter (Active && Posted && EffectiveDate ≤ StatementDate, on the cash account).
- Errors: auto-match (preview or apply) on a not-found or Completed reconciliation → 409 via `RequireOpenAsync` (consistent with clear/unclear/complete). `ArgumentException` → 422, `InvalidOperationException` → 409.
- Money is `decimal`. Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Confirmed Slice 1 surface (extend these)

- `ReconciliationService` (`…/Accounting101.Banking.Reconciliation/ReconciliationService.cs`): ctor `(IBankStatementStore statements, IReconciliationStore reconciliations, IReconciliationLedgerReader ledger)`. Private helpers: `RequireOpenAsync(clientId, recId, ct)` (throws `InvalidOperationException` if not-found or Completed), `EligibleEntriesAsync(clientId, reconciliation, ct)→Task<IReadOnlyList<EntryResponse>>`, `BuildWorksheetAsync`. Public `ClearAsync(clientId, recId, IReadOnlyList<Guid> entryIds, ct)→Task<ReconciliationWorksheet>` (re-validates eligibility, dedups, saves, returns worksheet).
- `ReconciliationMath.CashEffect(EntryResponse entry, Guid cashAccountId)→decimal` (Debit +, Credit −).
- `BankStatementLine(DateOnly Date, decimal Amount, string Description, string? ExternalRef)` (domain). `Reconciliation.ClearedEntryIds : IReadOnlyList<Guid>`. `WorksheetEntry(Guid EntryId, DateOnly Date, string? Reference, string? SourceType, decimal CashEffect, bool Cleared)`. `ReconciliationWorksheet(Reconciliation, BankStatement, IReadOnlyList<WorksheetEntry> Entries, decimal BookBalance, decimal ClearedTotal, decimal ReconciledDifference, bool Balanced)`.
- `EntryResponse`: `Id`, `EffectiveDate`, `Status`, `Posting`, `Lines`, `Reference`, `SourceType` (Ledger.Contracts). `EntryLineResponse(Guid AccountId, string Direction, decimal Amount, IReadOnlyDictionary<string,Guid> Dimensions, string? LineMemo)`.
- `ReconciliationEndpoints.MapReconciliationEndpoints` (`…Reconciliation.Api/ReconciliationEndpoints.cs`): group `/clients/{clientId:guid}` `.RequireAuthorization()`. Existing `MutateAsync(Func<Task<ReconciliationWorksheet>>)` helper maps ArgumentException→422, InvalidOperationException→409.
- Tests (`…Reconciliation.Tests/`): `ReconciliationMathTests.cs`, `ReconciliationServiceTests.cs` (helpers `CashEntry(id, direction, amount)`, `Build(entries, bookBalance)→(svc, stmts, recs)`, `StatementBody(opening, closing, params (decimal amt, string desc)[])`), `Fakes.cs` (`FakeLedgerReader(IReadOnlyList<EntryResponse>, decimal)`), `ReconciliationE2eTests.cs` (helpers `SetUpChartAsync(controller, clientId, fixture)`, `ApproveBySourceRefAsync(reader, approver, clientId, sourceRef)`; fixture `SeedSodClientAsync()→(clientId, controller, clerk, approver)`, account ids `CashAccountId`/`MembersCapitalAccountId`/`InterestExpenseAccountId`).
- `EntryResponse` positional constructor used in tests (verbatim from `ReconciliationServiceTests.CashEntry`): `new(id, 0, date, "Standard", "Active", "Posted", 2, null, null, null, null, [lines…], SourceType: "Test", Reference: "R")`.

---

### Task 1: `AutoMatcher` pure matcher + DTOs (TDD)

**Files:**
- Create: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AutoMatcher.cs`
- Create: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AutoMatcherTests.cs`

**Interfaces:**
- Produces: `MatchableEntry`, `AutoMatch`, `UnmatchedLine`, `AutoMatchProposal`, and `AutoMatcher.Match(...)` — consumed by Task 2 (service) and Task 3 (endpoint result type).

- [ ] **Step 1: Write the failing matcher tests**

Create `AutoMatcherTests.cs`:
```csharp
using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class AutoMatcherTests
{
    private static BankStatementLine Line(decimal amount, int day = 15) =>
        new(new DateOnly(2026, 1, day), amount, $"line {amount}", null);

    private static MatchableEntry Entry(Guid id, decimal cashEffect, int day = 15) =>
        new(id, new DateOnly(2026, 1, day), cashEffect);

    [Fact]
    public void Pairs_lines_to_entries_by_exact_signed_amount()
    {
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        // +100 deposit line ↔ +100 entry; −40 payment line ↔ −40 entry.
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(100m), Line(-40m)],
            [Entry(dep, 100m), Entry(pay, -40m)]);

        Assert.Equal(2, p.Matches.Count);
        Assert.Empty(p.UnmatchedStatementLines);
        Assert.Empty(p.UnmatchedEntries);
        Assert.Contains(p.Matches, m => m.StatementLineIndex == 0 && m.EntryId == dep && m.Amount == 100m);
        Assert.Contains(p.Matches, m => m.StatementLineIndex == 1 && m.EntryId == pay && m.Amount == -40m);
        Assert.Equal(new[] { dep, pay }.Order(), p.MatchedEntryIds.Order()); // MatchedEntryIds mirrors Matches
    }

    [Fact]
    public void When_amounts_tie_it_picks_the_nearest_date_entry()
    {
        Guid near = Guid.NewGuid(), far = Guid.NewGuid();
        // One +100 line on day 15; two +100 entries (day 14 near, day 1 far) → near wins, far is unmatched.
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(100m, day: 15)],
            [Entry(far, 100m, day: 1), Entry(near, 100m, day: 14)]);

        Assert.Single(p.Matches);
        Assert.Equal(near, p.Matches[0].EntryId);
        Assert.Equal(1, p.Matches[0].DaysApart); // |14 − 15|
        Assert.Single(p.UnmatchedEntries);
        Assert.Equal(far, p.UnmatchedEntries[0].EntryId);
    }

    [Fact]
    public void A_line_with_no_matching_amount_is_reported_unmatched()
    {
        Guid e = Guid.NewGuid();
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(100m), Line(77m)],          // 77 has no entry
            [Entry(e, 100m)]);

        Assert.Single(p.Matches);
        Assert.Single(p.UnmatchedStatementLines);
        Assert.Equal(1, p.UnmatchedStatementLines[0].StatementLineIndex);
        Assert.Equal(77m, p.UnmatchedStatementLines[0].Amount);
        Assert.Empty(p.UnmatchedEntries);
    }

    [Fact]
    public void Each_entry_is_consumed_at_most_once()
    {
        Guid only = Guid.NewGuid();
        // Two +50 lines but only one +50 entry → one match, one unmatched line, no leftover entry.
        AutoMatchProposal p = AutoMatcher.Match(
            [Line(50m), Line(50m)],
            [Entry(only, 50m)]);

        Assert.Single(p.Matches);
        Assert.Single(p.UnmatchedStatementLines);
        Assert.Empty(p.UnmatchedEntries);
    }
}
```

- [ ] **Step 2: Run, verify it FAILS (no `AutoMatcher`/DTOs yet)**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~AutoMatcherTests" --nologo`
Expected: FAIL to compile (`AutoMatcher`, `MatchableEntry`, `AutoMatchProposal` not defined).

- [ ] **Step 3: Create `AutoMatcher.cs`**

```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>An eligible, uncleared ledger entry prepared for matching: its id, date, and signed book cash
/// effect (Debit-to-cash +, Credit −) — the value that must equal a bank statement line's signed amount.</summary>
public sealed record MatchableEntry(Guid EntryId, DateOnly Date, decimal CashEffect);

/// <summary>A proposed pairing of one statement line (by its index in the statement) to one ledger entry.</summary>
public sealed record AutoMatch(
    int StatementLineIndex, decimal Amount, Guid EntryId, DateOnly LineDate, DateOnly EntryDate, int DaysApart);

/// <summary>A statement line that no uncleared eligible entry matched, by index.</summary>
public sealed record UnmatchedLine(int StatementLineIndex, DateOnly Date, decimal Amount, string Description);

/// <summary>The auto-match proposal: the pairings, the lines and entries left unmatched on each side, and a
/// flat list of the matched entry ids (ready to hand to /clear). Pure data — auto-match mutates nothing.</summary>
public sealed record AutoMatchProposal(
    IReadOnlyList<AutoMatch> Matches,
    IReadOnlyList<UnmatchedLine> UnmatchedStatementLines,
    IReadOnlyList<MatchableEntry> UnmatchedEntries,
    IReadOnlyList<Guid> MatchedEntryIds);

/// <summary>Pairs bank statement lines to uncleared eligible ledger entries by exact signed amount, 1:1,
/// breaking amount ties by nearest date (then entry id, for determinism). Pure — no ledger or store access.</summary>
public static class AutoMatcher
{
    public static AutoMatchProposal Match(
        IReadOnlyList<BankStatementLine> statementLines, IReadOnlyList<MatchableEntry> uncleared)
    {
        List<MatchableEntry> remaining = uncleared.ToList();
        List<AutoMatch> matches = [];
        List<UnmatchedLine> unmatchedLines = [];

        for (int i = 0; i < statementLines.Count; i++)
        {
            BankStatementLine line = statementLines[i];
            MatchableEntry? best = remaining
                .Where(e => e.CashEffect == line.Amount)
                .OrderBy(e => Math.Abs(e.Date.DayNumber - line.Date.DayNumber))
                .ThenBy(e => e.EntryId)
                .FirstOrDefault();

            if (best is null)
            {
                unmatchedLines.Add(new UnmatchedLine(i, line.Date, line.Amount, line.Description));
                continue;
            }

            remaining.Remove(best);
            int daysApart = Math.Abs(best.Date.DayNumber - line.Date.DayNumber);
            matches.Add(new AutoMatch(i, line.Amount, best.EntryId, line.Date, best.Date, daysApart));
        }

        return new AutoMatchProposal(matches, unmatchedLines, remaining, matches.Select(m => m.EntryId).ToList());
    }
}
```

- [ ] **Step 4: Run, verify the matcher tests PASS**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~AutoMatcherTests" --nologo`
Expected: 4/4 PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AutoMatcher.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AutoMatcherTests.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): AutoMatcher — pair statement lines to entries by amount

Pure 1:1 matcher: exact signed-amount equality (bank line == entry cash
effect), nearest-date tiebreak, each entry consumed at most once. Reports
unmatched lines + entries and a flat matched-entry-id list. Unit-tested.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Service `AutoMatchAsync` + `AutoMatchApplyAsync` + service tests

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/ReconciliationService.cs`
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationServiceTests.cs`

**Interfaces:**
- Consumes: `AutoMatcher.Match`, `MatchableEntry`, `AutoMatchProposal` (Task 1); existing `RequireOpenAsync`, `EligibleEntriesAsync`, `ClearAsync`, `ReconciliationMath.CashEffect`.
- Produces: `AutoMatchAsync`, `AutoMatchApplyAsync` (consumed by Task 3 endpoint).

- [ ] **Step 1: Write the failing service tests**

Append these tests to `ReconciliationServiceTests.cs` (inside the class, after the existing `[Fact]`s — they reuse the existing `Cash`, `StmtDate`, `CashEntry`, `Build`, `StatementBody` helpers):
```csharp
    [Fact]
    public async Task Auto_match_proposes_entries_by_amount_and_excludes_already_cleared()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m), CashEntry(pay, "Credit", 40m)];
        (ReconciliationService svc, _, _) = Build(entries, 60m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 60m, (100m, "dep"), (-40m, "pay")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        // Nothing cleared yet → both lines pair to their entries.
        AutoMatchProposal full = await svc.AutoMatchAsync(clientId, rec.Id);
        Assert.Equal(2, full.Matches.Count);
        Assert.Empty(full.UnmatchedStatementLines);
        Assert.Equal(new[] { dep, pay }.Order(), full.MatchedEntryIds.Order());

        // Clear the deposit manually → auto-match now proposes only the payment.
        await svc.ClearAsync(clientId, rec.Id, [dep]);
        AutoMatchProposal partial = await svc.AutoMatchAsync(clientId, rec.Id);
        Assert.Single(partial.Matches);
        Assert.Equal(pay, partial.Matches[0].EntryId);
        Assert.DoesNotContain(partial.UnmatchedEntries, e => e.EntryId == dep); // cleared entry not offered
    }

    [Fact]
    public async Task Auto_match_apply_clears_the_matches_and_balances()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m), CashEntry(pay, "Credit", 40m)];
        (ReconciliationService svc, _, _) = Build(entries, 60m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 60m, (100m, "dep"), (-40m, "pay")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        ReconciliationWorksheet after = await svc.AutoMatchApplyAsync(clientId, rec.Id);
        Assert.Equal(60m, after.ClearedTotal);
        Assert.Equal(0m, after.ReconciledDifference);
        Assert.True(after.Balanced);
        Assert.All(after.Entries, e => Assert.True(e.Cleared));
    }

    [Fact]
    public async Task Auto_match_on_a_completed_reconciliation_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m)];
        (ReconciliationService svc, _, _) = Build(entries, 100m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 100m, (100m, "dep")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);
        await svc.AutoMatchApplyAsync(clientId, rec.Id);
        await svc.CompleteAsync(clientId, rec.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AutoMatchAsync(clientId, rec.Id));
    }
```

- [ ] **Step 2: Run, verify they FAIL (no `AutoMatchAsync` yet)**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~ReconciliationServiceTests" --nologo`
Expected: FAIL to compile (`AutoMatchAsync`/`AutoMatchApplyAsync` not defined).

- [ ] **Step 3: Add the service methods**

In `ReconciliationService.cs`, add these two public methods (place them after `CompleteAsync`, before the private `RequireOpenAsync`):
```csharp
    /// <summary>Propose 1:1 pairings of the statement's lines to the uncleared eligible entries, by signed
    /// amount (nearest-date tiebreak). Read-only — proposes, never clears.</summary>
    public async Task<AutoMatchProposal> AutoMatchAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        Reconciliation reconciliation = await RequireOpenAsync(clientId, reconciliationId, ct);
        BankStatement statement = (await statements.GetAsync(clientId, reconciliation.BankStatementId, ct))!;
        IReadOnlyList<EntryResponse> eligible = await EligibleEntriesAsync(clientId, reconciliation, ct);
        var clearedIds = reconciliation.ClearedEntryIds.ToHashSet();
        List<MatchableEntry> uncleared = eligible
            .Where(e => !clearedIds.Contains(e.Id))
            .Select(e => new MatchableEntry(e.Id, e.EffectiveDate, ReconciliationMath.CashEffect(e, reconciliation.CashAccountId)))
            .ToList();
        return AutoMatcher.Match(statement.Lines, uncleared);
    }

    /// <summary>Run the auto-match and clear the matched entries (through the validated <see cref="ClearAsync"/>),
    /// returning the updated worksheet.</summary>
    public async Task<ReconciliationWorksheet> AutoMatchApplyAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        AutoMatchProposal proposal = await AutoMatchAsync(clientId, reconciliationId, ct);
        return await ClearAsync(clientId, reconciliationId, proposal.MatchedEntryIds, ct);
    }
```

- [ ] **Step 4: Run, verify all service tests PASS**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~ReconciliationServiceTests" --nologo`
Expected: all PASS (4 existing + 3 new = 7).

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/ReconciliationService.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): AutoMatchAsync + AutoMatchApplyAsync on the service

AutoMatchAsync projects uncleared eligible entries to MatchableEntry (cash
effect) and runs AutoMatcher (read-only proposal). AutoMatchApplyAsync clears
the matched ids through the validated ClearAsync and returns the worksheet.
Rejects a completed reconciliation via RequireOpenAsync.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Endpoint (`auto-match` with `apply` flag) + E2E

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs`
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationE2eTests.cs`

**Interfaces:**
- Consumes: `ReconciliationService.AutoMatchAsync`/`AutoMatchApplyAsync` (Task 2); `AutoMatchProposal` (Task 1); existing E2E helpers + fixture.

- [ ] **Step 1: Add the endpoint**

In `ReconciliationEndpoints.cs`, register the route inside `MapReconciliationEndpoints` (after the `/complete` line):
```csharp
        clients.MapPost("/reconciliations/{id:guid}/auto-match", AutoMatch);
```
and add the handler (after the `Complete` method):
```csharp
    private static async Task<IResult> AutoMatch(
        Guid clientId, Guid id, bool? apply, ReconciliationService service, CancellationToken ct)
    {
        try
        {
            return apply == true
                ? Results.Ok(await service.AutoMatchApplyAsync(clientId, id, ct))   // clears matches → worksheet
                : Results.Ok(await service.AutoMatchAsync(clientId, id, ct));        // read-only proposal
        }
        catch (InvalidOperationException ex) // reconciliation not found or already completed
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex) // ineligible id on the apply→clear path (not expected in normal operation)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
```
(`bool? apply` binds the optional `?apply=` query param — absent → null → preview; `?apply=true` → apply.)

- [ ] **Step 2: Build the Api project**

Run: `dotnet build Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/Accounting101.Banking.Reconciliation.Api.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 3: Add the E2E test**

Append this `[Fact]` to `ReconciliationE2eTests.cs` (inside the class — it reuses the existing `SetUpChartAsync`, `ApproveBySourceRefAsync`, the fixture, and the request types already imported at the top of the file):
```csharp
    [Fact]
    public async Task Auto_match_proposes_then_applies_to_balance_the_reconciliation()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly date = new(2026, 1, 20);
        DateOnly stmtDate = new(2026, 1, 31);

        // Real deposit (Dr Cash 100) + disbursement (Cr Cash 40), both approved → posted.
        CashDeposit dep = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
                new RecordCashDepositRequest([new CashLineRequest(fixture.MembersCapitalAccountId, 100m)], date, "DEP", null)))
            .Content.ReadFromJsonAsync<CashDeposit>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dep.Id);
        CashDisbursement dis = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements",
                new RecordCashDisbursementRequest([new CashLineRequest(fixture.InterestExpenseAccountId, 40m)], date, "DIS", null)))
            .Content.ReadFromJsonAsync<CashDisbursement>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dis.Id);

        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 60m,
                    [new BankStatementLineRequest(date, 100m, "deposit", null), new BankStatementLineRequest(date, -40m, "payment", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        // Preview: proposes both pairings, nothing unmatched, mutates nothing.
        AutoMatchProposal proposal = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/auto-match", null))
            .Content.ReadFromJsonAsync<AutoMatchProposal>())!;
        Assert.Equal(2, proposal.Matches.Count);
        Assert.Empty(proposal.UnmatchedStatementLines);
        Assert.Empty(proposal.UnmatchedEntries);

        // Preview left the reconciliation untouched — nothing cleared yet.
        ReconciliationWorksheet preview = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Assert.All(preview.Entries, e => Assert.False(e.Cleared));

        // Apply: clears the matches → balanced.
        ReconciliationWorksheet applied = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/auto-match?apply=true", null))
            .Content.ReadFromJsonAsync<ReconciliationWorksheet>())!;
        Assert.Equal(60m, applied.ClearedTotal);
        Assert.Equal(0m, applied.ReconciledDifference);
        Assert.True(applied.Balanced);

        // complete now succeeds.
        Reconciliation done = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null))
            .Content.ReadFromJsonAsync<Reconciliation>())!;
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }
```

- [ ] **Step 4: Run the full Reconciliation test project**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --nologo`
Expected: all PASS (3 math + 4 matcher + 7 service + 3 E2E = 17).

- [ ] **Step 5: Build the whole solution — confirm no regressions**

Run: `dotnet build Accounting101.slnx --nologo`
Expected: Build succeeded (only pre-existing NU19xx transitive warnings).

- [ ] **Step 6: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationE2eTests.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): auto-match endpoint (preview + ?apply) with E2E

POST /reconciliations/{id}/auto-match returns the proposal (read-only);
?apply=true clears the matched entries and returns the worksheet. E2E proves
preview proposes both pairings and mutates nothing, then apply balances and
complete succeeds.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- Pure matcher (exact signed amount, 1:1, nearest-date tiebreak) → Task 1 (`AutoMatcher`). ✓
- `AutoMatchProposal` DTO (matches / unmatched lines / unmatched entries / matched ids) → Task 1. ✓
- Service `AutoMatchAsync` (read-only, excludes cleared) + `AutoMatchApplyAsync` (via `ClearAsync`) → Task 2. ✓
- Endpoint with `apply` flag; preview vs apply; 409 on not-open → Task 3. ✓
- Read-only on the GL (no PostAsync; apply only edits the cleared set) → service uses the read-only reader + `ClearAsync`. ✓
- Unit + service + E2E tests → Tasks 1/2/3. ✓

**2. Placeholder scan:** No TBD/TODO; full code for every file; commands explicit. The new tests reuse named helpers that exist in the files being modified (verified against the Slice 1 source quoted in "Confirmed Slice 1 surface").

**3. Type consistency:** `AutoMatcher.Match(IReadOnlyList<BankStatementLine>, IReadOnlyList<MatchableEntry>)→AutoMatchProposal` defined in Task 1 and consumed unchanged in Task 2; `AutoMatchAsync`/`AutoMatchApplyAsync` signatures defined in Task 2 and consumed unchanged in Task 3. `MatchableEntry(Guid EntryId, DateOnly Date, decimal CashEffect)`, `AutoMatch(int StatementLineIndex, decimal Amount, Guid EntryId, DateOnly LineDate, DateOnly EntryDate, int DaysApart)`, `UnmatchedLine(int, DateOnly, decimal, string)`, `AutoMatchProposal(Matches, UnmatchedStatementLines, UnmatchedEntries, MatchedEntryIds)` are used identically across tests + code. `EntryResponse`/`BankStatementLine`/`ClearAsync`/`CashEffect`/`RequireOpenAsync`/`EligibleEntriesAsync` match the quoted Slice 1 source. The E2E reuses the existing `SetUpChartAsync`/`ApproveBySourceRefAsync` helpers and request DTOs already imported in `ReconciliationE2eTests.cs`. ✓
