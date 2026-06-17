using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Contracts;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// The ledger HTTP surface, CQRS-shaped: command verbs and resource reads under
/// <c>/clients/{clientId}</c>. Every endpoint authenticates (the scheme), authorizes (control-DB
/// membership), resolves the client's database, and threads the authenticated actor into the engine.
/// Authorization and segregation-of-duties live here, in the host — the engine records, it does not police.
/// </summary>
public static class LedgerEndpoints
{
    public static void MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app
            .MapGroup("/clients/{clientId:guid}")
            .RequireAuthorization();

        clients.MapPost("/entries", PostEntry);
        clients.MapGet("/entries/{entryId:guid}", GetEntry);
        clients.MapGet("/audit/{entryId:guid}", GetAudit);
    }

    private static async Task<IResult> PostEntry(
        Guid clientId,
        PostEntryRequest request,
        ClaimsPrincipal user,
        IActorFactory actorFactory,
        ControlStore control,
        ClientLedgerFactory ledgers,
        CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        if (!await control.IsMemberAsync(actor.UserId, clientId, cancellationToken))
            return Results.Forbid();

        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        if (ledger is null)
            return Results.NotFound();

        JournalEntry entry;
        try
        {
            entry = MapEntry(clientId, request, actor);
        }
        catch (Exception ex) when (ex is ArgumentException or UnbalancedEntryException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            await ledger.Service.PostAsync(entry, actor, cancellationToken);
        }
        catch (InvalidOperationException ex) // closed-period freeze
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Results.Problem(
                "An entry with this id or sequence number already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Created(
            $"/clients/{clientId}/entries/{entry.Id}",
            new PostEntryResponse(entry.Id, entry.Status.ToString(), entry.Posting.ToString()));
    }

    private static async Task<IResult> GetEntry(
        Guid clientId,
        Guid entryId,
        ClaimsPrincipal user,
        IActorFactory actorFactory,
        ControlStore control,
        ClientLedgerFactory ledgers,
        CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        if (!await control.IsMemberAsync(actor.UserId, clientId, cancellationToken))
            return Results.Forbid();

        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        if (ledger is null)
            return Results.NotFound();

        JournalEntry? entry = await ledger.Journal.GetAsync(entryId, cancellationToken);
        if (entry is null || entry.ClientId != clientId)
            return Results.NotFound();

        return Results.Ok(new EntryResponse(
            entry.Id,
            entry.SequenceNumber,
            entry.EffectiveDate,
            entry.Type.ToString(),
            entry.Status.ToString(),
            entry.Posting.ToString(),
            entry.Lines.Count));
    }

    private static async Task<IResult> GetAudit(
        Guid clientId,
        Guid entryId,
        ClaimsPrincipal user,
        IActorFactory actorFactory,
        ControlStore control,
        ClientLedgerFactory ledgers,
        CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        if (!await control.IsMemberAsync(actor.UserId, clientId, cancellationToken))
            return Results.Forbid();

        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        if (ledger is null)
            return Results.NotFound();

        var records = await ledger.Audit.GetForEntryAsync(clientId, entryId, cancellationToken);
        List<AuditRecordResponse> response = records
            .Select(r => new AuditRecordResponse(
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

        return Results.Ok(response);
    }

    private static JournalEntry MapEntry(Guid clientId, PostEntryRequest request, Actor actor)
    {
        List<Line> lines = request.Lines
            .Select(l => new Line
            {
                Id = Guid.NewGuid(),
                AccountId = l.AccountId,
                Direction = Enum.Parse<Direction>(l.Direction, ignoreCase: true),
                Amount = l.Amount,
            })
            .ToList();

        return JournalEntry.Create(
            id: request.Id ?? Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: request.SequenceNumber,
            effectiveDate: request.EffectiveDate,
            postedAt: DateTimeOffset.UtcNow,
            type: EntryType.Standard,
            audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines: lines);
    }
}
