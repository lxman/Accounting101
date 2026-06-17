using System.Security.Claims;
using Accounting101.Ledger.Api.Contracts;
using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo;
using Accounting101.Ledger.Mongo.Documents;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// The ledger HTTP surface, CQRS-shaped: command verbs and resource reads under
/// <c>/clients/{clientId}</c>. Every endpoint authenticates (the scheme), authorizes (control-DB
/// membership via <see cref="LedgerGateway"/>), resolves the client's database, and threads the
/// authenticated actor into the engine. Authorization lives here, in the host — the engine records.
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
        clients.MapPost("/periods/close", ClosePeriod);

        // Queries
        clients.MapGet("/entries", ListEntries);
        clients.MapGet("/entries/{entryId:guid}", GetEntry);
        clients.MapGet("/trial-balance", GetTrialBalance);
        clients.MapGet("/accounts/{accountId:guid}/balance", GetAccountBalance);
        clients.MapGet("/audit", GetClientAudit);
        clients.MapGet("/audit/verify", VerifyAudit);
        clients.MapGet("/audit/{entryId:guid}", GetEntryAudit);
    }

    // ---- Commands ---------------------------------------------------------------------------

    private static async Task<IResult> PostEntry(
        Guid clientId, PostEntryRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry entry;
        try
        {
            entry = MapEntry(clientId, request, ctx.Actor);
        }
        catch (Exception ex) when (ex is ArgumentException or UnbalancedEntryException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            await ctx.Ledger.Service.PostAsync(entry, ctx.Actor, cancellationToken);
        }
        catch (InvalidOperationException ex) // closed-period freeze
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
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
        Guid clientId, Guid entryId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        if (await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken) is null)
            return Results.NotFound();

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
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
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
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
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
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            JournalEntry result = await ctx.Ledger.Service.ReviseAsync(originalId, replacement, ctx.Actor, request.Reason, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{result.Id}", ToEntryResponse(result));
        }
        catch (InvalidOperationException ex) // closed-period freeze
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
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
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

    // ---- Queries ----------------------------------------------------------------------------

    private static async Task<IResult> ListEntries(
        Guid clientId, Guid? account, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        IReadOnlyList<JournalEntry> entries = account is { } accountId
            ? await ctx.Ledger.Journal.GetTouchingAccountAsync(clientId, accountId, cancellationToken)
            : await ctx.Ledger.Journal.GetByClientAsync(clientId, cancellationToken);

        return Results.Ok(entries.Select(ToEntryResponse).ToList());
    }

    private static async Task<IResult> GetEntry(
        Guid clientId, Guid entryId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? entry = await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken);
        return entry is null || entry.ClientId != clientId
            ? Results.NotFound()
            : Results.Ok(ToEntryResponse(entry));
    }

    private static async Task<IResult> GetTrialBalance(
        Guid clientId, DateOnly? asOf, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
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
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        decimal balance = await ctx.Ledger.Projection.GetBalanceAsync(clientId, accountId, cancellationToken);
        return Results.Ok(new AccountBalanceResponse(accountId, balance));
    }

    private static async Task<IResult> GetClientAudit(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        return Results.Ok(ToAuditResponses(await ctx.Ledger.Audit.GetForClientAsync(clientId, cancellationToken)));
    }

    private static async Task<IResult> GetEntryAudit(
        Guid clientId, Guid entryId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        return Results.Ok(ToAuditResponses(await ctx.Ledger.Audit.GetForEntryAsync(clientId, entryId, cancellationToken)));
    }

    private static async Task<IResult> VerifyAudit(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        bool valid = await ctx.Ledger.Audit.VerifyAsync(clientId, cancellationToken);
        return Results.Ok(new AuditVerifyResponse(valid));
    }

    // ---- Mapping ----------------------------------------------------------------------------

    private static IResult Conflict(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status409Conflict);

    private static EntryResponse ToEntryResponse(JournalEntry e) => new(
        e.Id, e.SequenceNumber, e.EffectiveDate,
        e.Type.ToString(), e.Status.ToString(), e.Posting.ToString(),
        e.Lines.Count, e.Supersedes, e.SupersededBy);

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
        }).ToList();
}
