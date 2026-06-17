using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Contracts;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo;
using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// The ledger HTTP surface, CQRS-shaped: command verbs and resource reads under
/// <c>/clients/{clientId}</c>. Every endpoint resolves through <see cref="LedgerGateway"/>, which
/// authenticates, checks the caller's role holds the required <see cref="Permission"/>, and resolves
/// the client's database. Segregation of duties (approver ≠ author) is enforced on top, at approval.
/// </summary>
public static class LedgerEndpoints
{
    public static void MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app
            .MapGroup("/clients/{clientId:guid}")
            .RequireAuthorization();

        // Commands
        clients.MapPost("/entries", PostEntry);
        clients.MapPost("/entries/{entryId:guid}/approve", ApproveEntry);
        clients.MapPost("/entries/{entryId:guid}/void", VoidEntry);
        clients.MapPost("/entries/{originalId:guid}/revise", ReviseEntry);
        clients.MapPost("/entries/{originalId:guid}/reverse", ReverseEntry);
        clients.MapPost("/periods/close", ClosePeriod);
        clients.MapPost("/periods/close-year", CloseYear);
        clients.MapPost("/periods/reopen", Reopen).RequireAuthorization(StepUpAuthorizationHandler.Policy);
        clients.MapPut("/accounts/{accountId:guid}", UpsertAccount);
        clients.MapPost("/onboarding", Onboard);

        // Queries
        clients.MapGet("/entries", ListEntries);
        clients.MapGet("/entries/{entryId:guid}", GetEntry);
        clients.MapGet("/trial-balance", GetTrialBalance);
        clients.MapGet("/accounts", ListAccounts);
        clients.MapGet("/accounts/{accountId:guid}", GetAccount);
        clients.MapGet("/accounts/{accountId:guid}/balance", GetAccountBalance);
        clients.MapGet("/audit", GetClientAudit);
        clients.MapGet("/audit/verify", VerifyAudit);
        clients.MapGet("/audit/{entryId:guid}", GetEntryAudit);
    }

    // ---- Commands ---------------------------------------------------------------------------

    private static async Task<IResult> PostEntry(
        Guid clientId, PostEntryRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Post, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry entry;
        try
        {
            entry = MapEntry(clientId, request, ctx.Actor);
        }
        catch (Exception ex) when (ex is ArgumentException or UnbalancedEntryException)
        {
            return Unprocessable(ex.Message);
        }

        if (await ChartViolationsAsync(ctx.Ledger.Accounts, clientId, entry.Lines, cancellationToken) is { } violation)
            return violation;

        try
        {
            await ctx.Ledger.Service.PostAsync(entry, ctx.Actor, cancellationToken);
        }
        catch (InvalidOperationException ex) // closed-period freeze
        {
            return Conflict(ex.Message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict("An entry with this id or sequence number already exists.");
        }

        return Results.Created(
            $"/clients/{clientId}/entries/{entry.Id}",
            new PostEntryResponse(entry.Id, entry.Status.ToString(), entry.Posting.ToString()));
    }

    private static async Task<IResult> ApproveEntry(
        Guid clientId, Guid entryId, LedgerGateway gateway, ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Approve, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? entry = await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken);
        if (entry is null)
            return Results.NotFound();

        // Segregation of duties (host policy, per client): the approver may not be the author. This
        // also covers revisions and reversals, since they are approved through this same endpoint.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client?.RequireSegregationOfDuties == true && entry.Audit.CreatedBy == ctx.Actor.UserId)
            return Results.Problem(
                "Segregation of duties: an entry must be approved by someone other than the person who entered it.",
                statusCode: StatusCodes.Status403Forbidden);

        try
        {
            JournalEntry approved = await ctx.Ledger.Service.ApproveAsync(entryId, ctx.Actor, cancellationToken);
            return Results.Ok(ToEntryResponse(approved));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    private static async Task<IResult> VoidEntry(
        Guid clientId, Guid entryId, string? reason, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Void, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        if (await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken) is null)
            return Results.NotFound();

        try
        {
            JournalEntry voided = await ctx.Ledger.Service.VoidAsync(entryId, ctx.Actor, reason, cancellationToken);
            return Results.Ok(ToEntryResponse(voided));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    private static async Task<IResult> ReviseEntry(
        Guid clientId, Guid originalId, ReviseRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Revise, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        if (await ctx.Ledger.Journal.GetAsync(originalId, cancellationToken) is null)
            return Results.NotFound();

        JournalEntry replacement;
        try
        {
            replacement = MapReplacement(clientId, originalId, request, ctx.Actor);
        }
        catch (Exception ex) when (ex is ArgumentException or UnbalancedEntryException)
        {
            return Unprocessable(ex.Message);
        }

        if (await ChartViolationsAsync(ctx.Ledger.Accounts, clientId, replacement.Lines, cancellationToken) is { } violation)
            return violation;

        try
        {
            JournalEntry result = await ctx.Ledger.Service.ReviseAsync(originalId, replacement, ctx.Actor, request.Reason, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{result.Id}", ToEntryResponse(result));
        }
        catch (InvalidOperationException ex) // closed-period freeze, or original no longer active
        {
            return Conflict(ex.Message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict("An entry with this id or sequence number already exists.");
        }
    }

    private static async Task<IResult> ReverseEntry(
        Guid clientId, Guid originalId, ReverseRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Reverse, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        if (await ctx.Ledger.Journal.GetAsync(originalId, cancellationToken) is null)
            return Results.NotFound();

        try
        {
            JournalEntry reversal = await ctx.Ledger.Service.ReverseAsync(
                originalId, request.ReversalDate, request.SequenceNumber, ctx.Actor, request.Reason, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{reversal.Id}", ToEntryResponse(reversal));
        }
        catch (InvalidOperationException ex) // not reversible, or reversal date in a closed period
        {
            return Conflict(ex.Message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict("An entry with this id or sequence number already exists.");
        }
    }

    private static async Task<IResult> ClosePeriod(
        Guid clientId, ClosePeriodRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Close, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        try
        {
            IReadOnlyDictionary<Guid, decimal> balances = await ctx.Ledger.Service.CloseAsync(clientId, request.AsOf, ctx.Actor, cancellationToken);
            return Results.Ok(new CloseResponse(request.AsOf, ToAccountBalances(balances)));
        }
        catch (InvalidOperationException ex) // already closed through >= AsOf
        {
            return Conflict(ex.Message);
        }
    }

    private static async Task<IResult> Reopen(
        Guid clientId, ReopenRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Reopen, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        try
        {
            await ctx.Ledger.Service.ReopenAsync(clientId, request.ReopenThrough, ctx.Actor, request.Reason, cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex) // nothing to reopen, or not earlier than the current close
        {
            return Conflict(ex.Message);
        }
    }

    private static async Task<IResult> Onboard(
        Guid clientId, OnboardingRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.ManageAccounts, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        List<Line> lines = MapOpeningLines(request.Balances);

        if (await ChartViolationsAsync(ctx.Ledger.Accounts, clientId, lines, cancellationToken) is { } violation)
            return violation;

        try
        {
            JournalEntry opening = await ctx.Ledger.Service.OpenAsync(
                clientId, request.AsOf, lines, request.SequenceNumber, ctx.Actor, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{opening.Id}", ToEntryResponse(opening));
        }
        catch (Exception ex) when (ex is ArgumentException or UnbalancedEntryException) // an opening trial balance must balance
        {
            return Unprocessable(ex.Message);
        }
        catch (InvalidOperationException ex) // cutover lands in a closed period
        {
            return Conflict(ex.Message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict("An entry with this id or sequence number already exists.");
        }
    }

    // ---- Queries ----------------------------------------------------------------------------

    private static async Task<IResult> ListEntries(
        Guid clientId, Guid? account, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        IReadOnlyList<JournalEntry> entries = account is { } accountId
            ? await ctx.Ledger.Journal.GetTouchingAccountAsync(clientId, accountId, cancellationToken)
            : await ctx.Ledger.Journal.GetByClientAsync(clientId, cancellationToken);

        return Results.Ok(entries.Select(ToEntryResponse).ToList());
    }

    private static async Task<IResult> GetEntry(
        Guid clientId, Guid entryId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? entry = await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken);
        return entry is null || entry.ClientId != clientId
            ? Results.NotFound()
            : Results.Ok(ToEntryResponse(entry));
    }

    private static async Task<IResult> GetTrialBalance(
        Guid clientId, DateOnly? asOf, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Current balances come from the O(1) projection; an as-of balance is a point-in-time
        // journal fold (the projection only holds "now").
        IReadOnlyDictionary<Guid, decimal> balances = asOf is { } asOfDate
            ? await ctx.Ledger.Journal.AggregateBalancesAsync(clientId, asOfDate, cancellationToken)
            : await ctx.Ledger.Projection.GetTrialBalanceAsync(clientId, cancellationToken);

        return Results.Ok(new TrialBalanceResponse(asOf, ToAccountBalances(balances)));
    }

    private static async Task<IResult> GetAccountBalance(
        Guid clientId, Guid accountId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        decimal balance = await ctx.Ledger.Projection.GetBalanceAsync(clientId, accountId, cancellationToken);
        return Results.Ok(new AccountBalanceResponse(accountId, balance));
    }

    private static async Task<IResult> GetClientAudit(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        return Results.Ok(ToAuditResponses(await ctx.Ledger.Audit.GetForClientAsync(clientId, cancellationToken)));
    }

    private static async Task<IResult> GetEntryAudit(
        Guid clientId, Guid entryId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        return Results.Ok(ToAuditResponses(await ctx.Ledger.Audit.GetForEntryAsync(clientId, entryId, cancellationToken)));
    }

    private static async Task<IResult> VerifyAudit(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        bool valid = await ctx.Ledger.Audit.VerifyAsync(clientId, cancellationToken);
        return Results.Ok(new AuditVerifyResponse(valid));
    }

    // ---- Chart of accounts + year-end -------------------------------------------------------

    private static async Task<IResult> UpsertAccount(
        Guid clientId, Guid accountId, AccountRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.ManageAccounts, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        Account account;
        try
        {
            account = MapAccount(clientId, accountId, request);
        }
        catch (ArgumentException ex) // unknown Type / RequiredDimension
        {
            return Unprocessable(ex.Message);
        }

        // Validate the resulting chart before persisting, so an invalid chart can never be stored.
        ChartOfAccounts current = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        List<Account> proposed = current.Accounts.Where(a => a.Id != accountId).Append(account).ToList();
        try
        {
            _ = new ChartOfAccounts(proposed);
        }
        catch (InvalidChartOfAccountsException ex)
        {
            return Unprocessable(ex.Message);
        }

        await ctx.Ledger.Accounts.UpsertAsync(account, cancellationToken);
        return Results.Ok(ToAccountResponse(account));
    }

    private static async Task<IResult> ListAccounts(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        return Results.Ok(chart.Accounts.Select(ToAccountResponse).ToList());
    }

    private static async Task<IResult> GetAccount(
        Guid clientId, Guid accountId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        Account? account = await ctx.Ledger.Accounts.GetAsync(accountId, cancellationToken);
        return account is null || account.ClientId != clientId
            ? Results.NotFound()
            : Results.Ok(ToAccountResponse(account));
    }

    private static async Task<IResult> CloseYear(
        Guid clientId, CloseYearRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Close, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        try
        {
            JournalEntry? closing = await ctx.Ledger.Service.CloseYearAsync(
                clientId, request.FiscalYearEnd, ctx.Actor, chart, request.ClosingSequenceNumber, cancellationToken);
            return Results.Ok(new CloseYearResponse(closing is null ? null : ToEntryResponse(closing)));
        }
        catch (InvalidOperationException ex) // already closed, or no retained-earnings account configured
        {
            return Conflict(ex.Message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict("The closing sequence number is already in use.");
        }
    }

    // ---- Mapping ----------------------------------------------------------------------------

    private static IResult Conflict(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status409Conflict);

    private static IResult Unprocessable(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static EntryResponse ToEntryResponse(JournalEntry e) => new(
        e.Id, e.SequenceNumber, e.EffectiveDate,
        e.Type.ToString(), e.Status.ToString(), e.Posting.ToString(),
        e.Lines.Count, e.Supersedes, e.SupersededBy, e.ReversalOf, e.ReversedBy);

    private static AccountResponse ToAccountResponse(Account a) => new(
        a.Id, a.Number, a.Name, a.Type.ToString(), a.ParentId, a.Postable,
        a.RequiredDimension?.ToString(), a.IsRetainedEarnings, a.Active, a.NormalSide.ToString(), a.IsTemporary);

    private static Account MapAccount(Guid clientId, Guid accountId, AccountRequest request) => new()
    {
        Id = accountId,
        ClientId = clientId,
        Number = request.Number,
        Name = request.Name,
        Type = Enum.Parse<AccountType>(request.Type, ignoreCase: true),
        ParentId = request.ParentId,
        Postable = request.Postable,
        RequiredDimension = request.RequiredDimension is null
            ? null
            : Enum.Parse<DimensionKind>(request.RequiredDimension, ignoreCase: true),
        IsRetainedEarnings = request.IsRetainedEarnings,
        Active = request.Active,
    };

    private static List<AccountBalanceResponse> ToAccountBalances(IReadOnlyDictionary<Guid, decimal> balances) =>
        balances.Select(kv => new AccountBalanceResponse(kv.Key, kv.Value)).ToList();

    private static List<AuditRecordResponse> ToAuditResponses(IEnumerable<AuditRecordDocument> records) =>
        records.Select(r => new AuditRecordResponse(
            r.Sequence,
            r.Action.ToString(),
            r.EntryId,
            r.EntryVersion,
            new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc), TimeSpan.Zero),
            r.Reason,
            new ActorResponse(
                r.Actor.UserId,
                r.Actor.Name,
                r.Actor.Claims.Select(c => new ClaimResponse(c.Type, c.Value)).ToList())))
        .ToList();

    private static JournalEntry MapEntry(Guid clientId, PostEntryRequest request, Actor actor) =>
        JournalEntry.Create(
            id: request.Id ?? Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: request.SequenceNumber,
            effectiveDate: request.EffectiveDate,
            postedAt: DateTimeOffset.UtcNow,
            type: EntryType.Standard,
            audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines: MapLines(request.Lines),
            reference: request.Reference,
            memo: request.Memo);

    private static JournalEntry MapReplacement(Guid clientId, Guid originalId, ReviseRequest request, Actor actor) =>
        JournalEntry.Create(
            id: request.Id ?? Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: request.SequenceNumber,
            effectiveDate: request.EffectiveDate,
            postedAt: DateTimeOffset.UtcNow,
            type: EntryType.Standard,
            audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines: MapLines(request.Lines),
            supersedes: originalId,
            reference: request.Reference,
            memo: request.Memo);

    private static List<Line> MapLines(IReadOnlyList<PostLineRequest> lines) =>
        lines.Select(l => new Line
        {
            Id = Guid.NewGuid(),
            AccountId = l.AccountId,
            Direction = Enum.Parse<Direction>(l.Direction, ignoreCase: true),
            Amount = l.Amount,
            CustomerId = l.CustomerId,
            VendorId = l.VendorId,
            ItemId = l.ItemId,
        }).ToList();

    private static List<Line> MapOpeningLines(IReadOnlyList<OpeningBalanceLine> balances) =>
        balances.Select(b => new Line
        {
            Id = Guid.NewGuid(),
            AccountId = b.AccountId,
            Direction = b.Balance >= 0m ? Direction.Debit : Direction.Credit,
            Amount = Math.Abs(b.Balance),
            CustomerId = b.CustomerId,
            VendorId = b.VendorId,
            ItemId = b.ItemId,
        }).ToList();

    /// <summary>
    /// Validate posted lines against the client's chart: each account must exist, be active and
    /// postable, and carry any dimension its control type requires. Skipped when no chart has been set
    /// up yet (nothing to validate against — keeps the onboarding bootstrap open). Returns a 422 result
    /// listing the violations, or null when the lines conform.
    /// </summary>
    private static async Task<IResult?> ChartViolationsAsync(
        MongoAccountStore accounts, Guid clientId, IReadOnlyList<Line> lines, CancellationToken cancellationToken)
    {
        ChartOfAccounts chart = await accounts.GetChartAsync(clientId, cancellationToken);
        if (chart.Accounts.Count == 0)
            return null;

        List<string> errors = [];
        foreach (Line line in lines)
        {
            Account? account = chart.Find(line.AccountId);
            if (account is null)
            {
                errors.Add($"Account {line.AccountId} is not in the chart of accounts.");
                continue;
            }

            if (!account.Active)
                errors.Add($"Account {account.Number} \"{account.Name}\" is inactive.");
            else if (!account.Postable)
                errors.Add($"Account {account.Number} \"{account.Name}\" is a summary account and cannot be posted to.");

            if (account.RequiredDimension is { } dimension && !LineHasDimension(line, dimension))
                errors.Add($"Account {account.Number} \"{account.Name}\" requires a {dimension} on the posting line.");
        }

        return errors.Count == 0 ? null : Unprocessable(string.Join(" ", errors));
    }

    private static bool LineHasDimension(Line line, DimensionKind dimension) => dimension switch
    {
        DimensionKind.Customer => line.CustomerId is not null,
        DimensionKind.Vendor => line.VendorId is not null,
        DimensionKind.Item => line.ItemId is not null,
        _ => true,
    };
}
