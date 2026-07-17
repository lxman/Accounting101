using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Core.Reporting;
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
        clients.MapPost("/entries/batch", PostBatch);
        clients.MapPost("/entries/validate", ValidateEntry);
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
        clients.MapGet("/subledger", GetSubledger);
        clients.MapGet("/subledger/reconciliation", GetSubledgerReconciliation);
        clients.MapGet("/statements/balance-sheet", GetBalanceSheet);
        clients.MapGet("/statements/income-statement", GetIncomeStatement);
        clients.MapGet("/statements/cash-flow", GetCashFlow);
        clients.MapGet("/accounts", ListAccounts);
        clients.MapGet("/accounts/{accountId:guid}", GetAccount);
        clients.MapGet("/dimensions", GetDimensions);
        clients.MapGet("/source-types", GetSourceTypes);
        clients.MapGet("/accounts/{accountId:guid}/balance", GetAccountBalance);
        clients.MapGet("/audit", GetClientAudit);
        clients.MapGet("/audit/verify", VerifyAudit);
        clients.MapGet("/audit/{entryId:guid}", GetEntryAudit);
    }

    // ---- Commands ---------------------------------------------------------------------------

    private static async Task<IResult> PostEntry(
        Guid clientId, PostEntryRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveForPostAsync(user, clientId, moduleAuth, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);

        // Fast idempotent-replay path: if the caller supplied an id and that entry already exists for this
        // client, short-circuit before freeze validation. The replay performs no write — no sequence $inc,
        // no transaction — so it is freeze-safe (a re-POST after period close returns the existing entry,
        // not a 409), and approval-safe (returns the entry in its current lifecycle state).
        if (request.Id is { } earlyId
            && await ctx.Ledger!.Service.GetEntryAsync(clientId, earlyId, cancellationToken) is { } earlyExisting)
        {
            if (!TryMapEntry(clientId, request, ctx.Actor!, ctx.ViaModule, out JournalEntry? earlyMapped, out Dictionary<string, string[]> earlyErrors))
                return ValidationProblem(earlyErrors);

            if (EntryComparison.SameFinancialContent(earlyExisting, earlyMapped!))
            {
                JournalEntry finalizedEarly = await FinalizeAsync(autoApprove, earlyExisting, ctx, cancellationToken);
                return Results.Ok(new PostEntryResponse(finalizedEarly.Id, finalizedEarly.Status.ToString(), finalizedEarly.Posting.ToString()));
            }

            return Unprocessable("An entry with this id already exists with different content.");
        }

        (Dictionary<string, string[]>? errs, IResult? conflict, JournalEntry? entry) =
            await ValidateForPostAsync(clientId, request, ctx, cancellationToken);
        if (errs is not null) return ValidationProblem(errs);
        if (conflict is not null) return conflict;

        try
        {
            await ctx.Ledger.Service.PostAsync(entry!, ctx.Actor, cancellationToken);
        }
        catch (InvalidOperationException ex) // closed-period freeze (authoritative transactional-time guard)
        {
            return Conflict(ex.Message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Idempotency is opt-in and caller-declared: only an explicit, caller-supplied id can collide on _id.
            if (request.Id is { } suppliedId
                && await ctx.Ledger!.Service.GetEntryAsync(clientId, suppliedId, cancellationToken) is { } existing)
            {
                // Same client + same financial content => idempotent replay of the same operation.
                if (EntryComparison.SameFinancialContent(existing, entry!))
                {
                    JournalEntry finalizedDup = await FinalizeAsync(autoApprove, existing, ctx, cancellationToken);
                    return Results.Ok(new PostEntryResponse(finalizedDup.Id, finalizedDup.Status.ToString(), finalizedDup.Posting.ToString()));
                }

                // Same id, different content => the caller reused an operation id for a different entry.
                return Unprocessable("An entry with this id already exists with different content.");
            }

            // No entry for this id under this client => a real conflict (sequence-number collision, or an id
            // already used by a different client). Do not leak the other entry.
            return Conflict("An entry with this id or sequence number already exists.");
        }

        JournalEntry finalizedEntry = await FinalizeAsync(autoApprove, entry!, ctx, cancellationToken);
        return Results.Created(
            $"/clients/{clientId}/entries/{finalizedEntry.Id}",
            new PostEntryResponse(finalizedEntry.Id, finalizedEntry.Status.ToString(), finalizedEntry.Posting.ToString()));
    }

    /// <summary>
    /// Side-effect-free dry run: runs the same pre-write validation as <see cref="PostEntry"/> and
    /// writes nothing. Returns <c>200 {valid:true}</c> when the entry would post, or the same
    /// 409/422 ProblemDetails a real post returns — byte-for-byte identical, guarded by the shared
    /// <see cref="ValidateForPostAsync"/> routine.
    /// </summary>
    private static async Task<IResult> ValidateEntry(
        Guid clientId, PostEntryRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveForPostAsync(user, clientId, moduleAuth, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        (Dictionary<string, string[]>? errs, IResult? conflict, _) =
            await ValidateForPostAsync(clientId, request, ctx, cancellationToken);
        if (errs is not null) return ValidationProblem(errs);
        return conflict ?? Results.Ok(new EntryValidationResponse(true));
    }

    /// <summary>
    /// The single pre-write validation routine shared by <see cref="PostEntry"/>, <see cref="ValidateEntry"/>,
    /// and <see cref="PostBatch"/>. Performs, in order:
    /// <list type="number">
    ///   <item>Map + balance check (<see cref="TryMapEntry"/> → <see cref="UnbalancedEntryException"/> → 422).</item>
    ///   <item>Chart validity (<see cref="ChartFieldViolationsAsync"/> — account exists, postable, required dimension present).</item>
    ///   <item>Period freeze (<see cref="LedgerService.EnsureOpenForPostAsync"/> → 409 on a closed period).</item>
    /// </list>
    /// Returns the raw error dictionary (not a prebuilt <see cref="IResult"/>) so a batch caller can
    /// re-key per-index field errors under an <c>entries[i].</c> prefix; a single-post caller wraps it
    /// in <see cref="ValidationProblem(IDictionary{string,string[]})"/>. The freeze check is not a field
    /// error — it surfaces as <c>Conflict</c>, a ready-made 409 <see cref="IResult"/>, since there is no
    /// field to key it on. Exactly one of <c>Errors</c>/<c>Conflict</c>/<c>Entry</c> is non-null. Never
    /// writes anything itself.
    /// </summary>
    private static async Task<(Dictionary<string, string[]>? Errors, IResult? Conflict, JournalEntry? Entry)> ValidateForPostAsync(
        Guid clientId, PostEntryRequest request, LedgerContext ctx, CancellationToken ct)
    {
        // ctx.Failed was checked by the caller before dispatching here, so Actor and Ledger are non-null.
        if (!TryMapEntry(clientId, request, ctx.Actor!, ctx.ViaModule, out JournalEntry? entry, out Dictionary<string, string[]> parseErrors))
            return (parseErrors, null, null);

        Dictionary<string, string[]> chartErrors = await ChartFieldViolationsAsync(ctx.Ledger!.Accounts, clientId, entry!.Lines, ct);
        if (chartErrors.Count > 0)
            return (chartErrors, null, null);

        try
        {
            await ctx.Ledger!.Service.EnsureOpenForPostAsync(clientId, entry.EffectiveDate, ct);
        }
        catch (InvalidOperationException ex) // closed-period freeze
        {
            return (null, Conflict(ex.Message), null);
        }

        return (null, null, entry);
    }

    private const int MaxBatchEntries = 500;

    /// <summary>
    /// Post many journal entries as one atomic business event (e.g. a payroll run). Mirrors
    /// <see cref="PostEntry"/>'s structure over a list: size guard → per-entry idempotency
    /// classification → validate-all → atomic write via <see cref="LedgerService.PostBatchAsync"/>.
    /// Every entry validates and writes, or none do — a single bad entry rolls back the whole batch,
    /// with the field errors keyed <c>entries[i].&lt;field&gt;</c> so the caller can locate it.
    /// </summary>
    private static async Task<IResult> PostBatch(
        Guid clientId, PostBatchRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveForPostAsync(user, clientId, moduleAuth, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);

        IReadOnlyList<PostEntryRequest> reqs = request.Entries ?? [];
        if (reqs.Count == 0) return Unprocessable("A batch must contain at least one entry.");
        if (reqs.Count > MaxBatchEntries) return Unprocessable($"A batch may contain at most {MaxBatchEntries} entries; got {reqs.Count}.");

        // Idempotency classification: look up every supplied id up front. All-present+content-match => replay;
        // none-present => write; any mix, or a present id with different content => 422 (ambiguous partial replay).
        int matchedExisting = 0;
        List<PostEntryResponse> replay = [];
        Dictionary<string, string[]> errors = [];
        for (int i = 0; i < reqs.Count; i++)
        {
            if (reqs[i].Id is not { } id) continue;
            JournalEntry? existing = await ctx.Ledger!.Service.GetEntryAsync(clientId, id, cancellationToken);
            if (existing is null) continue;

            if (!TryMapEntry(clientId, reqs[i], ctx.Actor!, ctx.ViaModule, out JournalEntry? mapped, out _)
                || !EntryComparison.SameFinancialContent(existing, mapped!))
            {
                errors[$"entries[{i}].id"] = ["An entry with this id already exists with different content."];
                continue;
            }
            matchedExisting++;
            replay.Add(new PostEntryResponse(existing.Id, existing.Status.ToString(), existing.Posting.ToString()));
        }
        if (errors.Count > 0) return ValidationProblem(errors);

        // Whole-batch replay only when EVERY entry carries an id AND every one already exists with matching content.
        if (matchedExisting > 0)
        {
            if (matchedExisting == reqs.Count) return Results.Ok(replay);
            return Unprocessable("Partial replay: some entries in this batch already exist and some do not. "
                               + "Re-submit the batch with all-new ids, or replay the exact original batch.");
        }

        // Validate every entry (map + chart + typo + balance + freeze), collecting per-index errors.
        JournalEntry[] mappedEntries = new JournalEntry[reqs.Count];
        for (int i = 0; i < reqs.Count; i++)
        {
            (Dictionary<string, string[]>? errs, IResult? conflict, JournalEntry? entry) =
                await ValidateForPostAsync(clientId, reqs[i], ctx, cancellationToken);
            if (errs is not null)
            {
                // Re-key the single-entry field errors under an entries[i]. prefix so the caller can locate the bad entry.
                foreach (KeyValuePair<string, string[]> kv in errs)
                    errors[$"entries[{i}].{kv.Key}"] = kv.Value;
                continue;
            }
            if (conflict is not null)
            {
                // A closed-period freeze has no field to key on — surface it under the entry's date.
                errors[$"entries[{i}].effectiveDate"] = ["This entry's effective date falls in a closed period."];
                continue;
            }
            mappedEntries[i] = entry!;
        }
        if (errors.Count > 0) return ValidationProblem(errors);

        try
        {
            IReadOnlyList<JournalEntry> written = await ctx.Ledger!.Service.PostBatchAsync(mappedEntries, ctx.Actor!, cancellationToken);
            List<PostEntryResponse> body = [];
            foreach (JournalEntry e in written)
            {
                JournalEntry finalized = await FinalizeAsync(autoApprove, e, ctx, cancellationToken);
                body.Add(new PostEntryResponse(finalized.Id, finalized.Status.ToString(), finalized.Posting.ToString()));
            }
            return Results.Created($"/clients/{clientId}/entries/batch", body);
        }
        catch (InvalidOperationException ex) // a freeze that raced past the pre-check
        {
            return Conflict(ex.Message);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict("An entry id or sequence number in this batch already exists.");
        }
    }

    /// <summary>True iff the client's resolved approval mode is AutoApprove (host policy, control DB).</summary>
    private static async Task<bool> AutoApproveAsync(Guid clientId, ControlStore control, CancellationToken ct)
    {
        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        return client is not null && ApprovalPolicy.ModeOf(client) == ApprovalMode.AutoApprove;
    }

    /// <summary>Under AutoApprove, approve a still-pending entry inline with the current actor (writing the
    /// Approved audit event) and return the approved entry; otherwise return it unchanged. Idempotent — a
    /// non-pending entry is returned untouched — so it is safe on both the fresh-post and replay paths.</summary>
    private static async Task<JournalEntry> FinalizeAsync(
        bool autoApprove, JournalEntry entry, LedgerContext ctx, CancellationToken ct) =>
        autoApprove && entry.Posting == PostingState.PendingApproval
            ? await ctx.Ledger!.Service.ApproveAsync(entry.Id, ctx.Actor!, ct)
            : entry;

    private static async Task<IResult> ApproveEntry(
        Guid clientId, Guid entryId, LedgerGateway gateway, ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Approve, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? entry = await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken);
        if (entry is null)
            return Results.NotFound();

        // Approval policy (host policy, per client). TwoPerson requires the approver to differ from the
        // author; this also covers revisions and reversals, approved through this same endpoint.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is not null && ApprovalPolicy.ModeOf(client) == ApprovalMode.TwoPerson
            && entry.Audit.CreatedBy == ctx.Actor.UserId)
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
        Guid clientId, Guid entryId, VoidRequest? request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveMemberAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? entry = await ctx.Ledger.Journal.GetAsync(entryId, cancellationToken);
        if (entry is null) return Results.NotFound();

        if (await gateway.AuthorizeEntryMutationAsync(
                user, clientId, entry.Audit.ViaModule, Permission.Void, moduleAuth, cancellationToken) is { } denied)
            return denied;

        try
        {
            JournalEntry voided = await ctx.Ledger.Service.VoidAsync(entryId, ctx.Actor, request?.Reason, cancellationToken);
            return Results.Ok(ToEntryResponse(voided));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    private static async Task<IResult> ReviseEntry(
        Guid clientId, Guid originalId, ReviseRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveMemberAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? entry = await ctx.Ledger.Journal.GetAsync(originalId, cancellationToken);
        if (entry is null) return Results.NotFound();

        if (await gateway.AuthorizeEntryMutationAsync(
                user, clientId, entry.Audit.ViaModule, Permission.Revise, moduleAuth, cancellationToken) is { } denied)
            return denied;

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
            bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);
            JournalEntry finalized = await FinalizeAsync(autoApprove, result, ctx, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{finalized.Id}", ToEntryResponse(finalized));
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
        Guid clientId, Guid originalId, ReverseRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
        ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveMemberAsync(user, clientId, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        JournalEntry? original = await ctx.Ledger.Journal.GetAsync(originalId, cancellationToken);
        if (original is null) return Results.NotFound();

        if (await gateway.AuthorizeEntryMutationAsync(
                user, clientId, original.Audit.ViaModule, Permission.Reverse, moduleAuth, cancellationToken) is { } denied)
            return denied;

        try
        {
            JournalEntry reversal = await ctx.Ledger.Service.ReverseAsync(
                originalId, request.ReversalDate, ctx.Actor, request.Reason,
                request.SourceRef, request.SourceType, cancellationToken);
            bool autoApprove = await AutoApproveAsync(clientId, control, cancellationToken);
            JournalEntry finalized = await FinalizeAsync(autoApprove, reversal, ctx, cancellationToken);
            return Results.Created($"/clients/{clientId}/entries/{finalized.Id}", ToEntryResponse(finalized));
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
        Guid clientId, ClosePeriodRequest request, LedgerGateway gateway, ControlStore control,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Close, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Fiscal-year-end guard (host policy, per client): the year-end is closed via close-year, which
        // also posts the closing entry — a plain monthly close there would strand it.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is not null)
        {
            DateOnly fye = FiscalYear.EndDateFor(FiscalYear.MonthOf(client), request.AsOf.Year);
            if (request.AsOf == fye)
                return Results.Problem(
                    detail: $"{request.AsOf:yyyy-MM-dd} is this client's fiscal year-end. Run the year-end close "
                          + $"(POST /clients/{clientId}/periods/close-year) instead of a monthly close.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["useEndpoint"] = "periods/close-year",
                        ["fiscalYearEnd"] = fye.ToString("yyyy-MM-dd"),
                    });
        }

        try
        {
            IReadOnlyDictionary<Guid, decimal> balances = await ctx.Ledger.Service.CloseAsync(clientId, request.AsOf, ctx.Actor, cancellationToken);
            return Results.Ok(new CloseResponse(request.AsOf, ToAccountBalances(balances)));
        }
        catch (PeriodCloseBlockedException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["blockers"] = ex.Blockers
                        .Select(b => new PendingEntryRef(b.Id, b.Reference, b.EffectiveDate, b.Type.ToString()))
                        .ToList(),
                });
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
                clientId, request.AsOf, lines, ctx.Actor, cancellationToken);
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
        Guid clientId, Guid? account, Guid? sourceRef, string? sourceRefs, string? dimension, Guid? value,
        string? posting, string? reference,
        int? skip, int? limit,
        LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Validate posting FIRST — an unrecognised value is always a 400, never silently ignored.
        PostingState? postingState = ParsePosting(posting, out IResult? badPosting);
        if (badPosting is not null) return badPosting;

        // Validate the batch sourceRefs CSV up front (present-but-empty is a valid empty list).
        List<Guid>? sourceRefList = null;
        if (sourceRefs is not null && !TryParseGuidCsv(sourceRefs, out sourceRefList, out IResult? badRefs))
            return badRefs;

        // Base-query precedence: reference → sourceRef → sourceRefs → dimension → account → posting-only → unfiltered.
        // The unfiltered and posting-only branches are pageable display paths (they may return an envelope).
        // The filtered branches (reference/sourceRef/sourceRefs/dimension/account) always return a bare array — they
        // are internal aggregation reads that module ledger clients consume directly.
        IReadOnlyList<JournalEntry> entries;
        bool postingHandledByQuery = false;
        bool pageable = false;
        PostingState ps = default;
        if (!string.IsNullOrWhiteSpace(reference))
            entries = await ctx.Ledger.Journal.GetByReferenceAsync(clientId, reference, cancellationToken);
        else if (sourceRef is { } source)
            entries = await ctx.Ledger.Journal.GetBySourceRefAsync(clientId, source, cancellationToken);
        else if (sourceRefList is not null)
            entries = await ctx.Ledger.Journal.GetBySourceRefsAsync(clientId, sourceRefList, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(dimension) && value is { } dimValue)
            entries = await ctx.Ledger.Journal.GetTouchingDimensionAsync(clientId, dimension, dimValue, cancellationToken);
        else if (account is { } accountId)
            entries = await ctx.Ledger.Journal.GetTouchingAccountAsync(clientId, accountId, cancellationToken);
        else if (postingState is { } pss)
        {
            ps = pss;
            entries = await ctx.Ledger.Journal.GetByPostingAsync(clientId, ps, Page(skip), PageLimit(limit), cancellationToken);
            postingHandledByQuery = true;
            pageable = true;
        }
        else
        {
            entries = await ctx.Ledger.Journal.GetByClientAsync(clientId, Page(skip), PageLimit(limit), cancellationToken);
            pageable = true;
        }

        // When postingState was provided but the base query was not the posting-only branch, refine in-memory.
        if (postingState is { } refine && !postingHandledByQuery)
            entries = entries.Where(e => e.Posting == refine).ToList();

        List<EntryResponse> items = entries.Select(ToEntryResponse).ToList();
        bool paged = skip is not null || limit is not null;
        if (pageable && paged)
        {
            long total = postingHandledByQuery
                ? await ctx.Ledger.Journal.CountByPostingAsync(clientId, ps, cancellationToken)
                : await ctx.Ledger.Journal.CountByClientAsync(clientId, cancellationToken);
            return Results.Ok(new PagedResponse<EntryResponse>(items, total, Page(skip), PageLimit(limit)));
        }
        return Results.Ok(items);
    }

    /// <summary>
    /// Parses the optional <paramref name="raw"/> posting filter.
    /// Returns a non-null <paramref name="error"/> when the value is present but not a recognised
    /// <see cref="PostingState"/> name; returns a null PostingState when <paramref name="raw"/> is absent.
    /// </summary>
    private static PostingState? ParsePosting(string? raw, out IResult? error)
    {
        if (raw is null)
        {
            error = null;
            return null;
        }
        if (Enum.TryParse<PostingState>(raw, ignoreCase: true, out PostingState parsed) &&
            parsed is PostingState.PendingApproval or PostingState.Posted)
        {
            error = null;
            return parsed;
        }
        error = Results.Problem("posting must be 'PendingApproval' or 'Posted'.", statusCode: 400);
        return null;
    }

    /// <summary>
    /// Parses a comma-separated Guid list (the <c>sourceRefs</c> batch filter). A present-but-empty or
    /// whitespace value is a valid empty list. Any non-empty element that is not a Guid yields a 400 in
    /// <paramref name="error"/> and a null list.
    /// </summary>
    private static bool TryParseGuidCsv(string raw, out List<Guid>? parsed, [NotNullWhen(false)] out IResult? error)
    {
        parsed = [];
        error = null;
        foreach (string part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(part, out Guid id))
            {
                parsed = null;
                error = Results.Problem($"'{part}' is not a valid Guid in sourceRefs.", statusCode: StatusCodes.Status400BadRequest);
                return false;
            }
            parsed.Add(id);
        }
        return true;
    }

    /// <summary>Page offset, never negative.</summary>
    private static int Page(int? skip) => skip is > 0 ? skip.Value : 0;

    /// <summary>Page size: defaults to 200, capped at 1000, so an unbounded scan can't be requested.</summary>
    private static int PageLimit(int? limit) => Math.Clamp(limit ?? 200, 1, 1000);

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

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        return Results.Ok(new TrialBalanceResponse(asOf, ToLabeledBalances(balances, chart)));
    }

    private static async Task<IResult> GetSubledger(
        Guid clientId, string? dimension, Guid? account, DateOnly? asOf,
        LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken,
        bool includePending = false)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        if (string.IsNullOrWhiteSpace(dimension))
            return Unprocessable("A subledger requires a 'dimension' type (e.g. Customer, Vendor, Employee).");

        // When an account is named, validate it is a control account requiring this exact dimension.
        // A null account is a legitimate cross-account query and bypasses this check.
        if (account is { } namedAccountId)
        {
            Account? namedAccount = await ctx.Ledger.Accounts.GetAsync(namedAccountId, cancellationToken);
            if (namedAccount is null || namedAccount.ClientId != clientId)
                return Results.NotFound();
            if (namedAccount.RequiredDimensions.Count == 0)
                return Unprocessable(
                    $"Account {namedAccountId} is not a control account requiring a dimension; subledger reconciliation does not apply. Use the account balance or journal instead.");
            if (!namedAccount.RequiredDimensions.Contains(dimension))
                return Unprocessable(
                    $"Account {namedAccountId} requires the '{string.Join(", ", namedAccount.RequiredDimensions)}' dimension(s), not '{dimension}'.");
        }

        IReadOnlyList<SubledgerBalance> balances = await ctx.Ledger.Journal.AggregateSubledgerAsync(
            clientId, dimension, account, asOf, includePending, cancellationToken);

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        return Results.Ok(new SubledgerResponse(
            dimension,
            asOf,
            balances.Select(b =>
            {
                Account? a = chart.Find(b.AccountId);
                return new SubledgerLineResponse(b.AccountId, b.DimensionValue, b.Balance, a?.Number, a?.Name);
            }).ToList()));
    }

    private static async Task<IResult> GetSubledgerReconciliation(
        Guid clientId, Guid? account, string? dimension, DateOnly? asOf,
        LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        if (account is not { } accountId)
            return Unprocessable("Reconciliation requires an 'account' — the control account to tie out.");
        if (string.IsNullOrWhiteSpace(dimension))
            return Unprocessable("Reconciliation requires a 'dimension' type.");

        // Validate the account exists and is a control account requiring the requested dimension.
        // Reconciliation on a plain account is meaningless — nothing is tagged, so the variance
        // would equal the whole balance, which is misleading rather than useful.
        Account? controlAccount = await ctx.Ledger.Accounts.GetAsync(accountId, cancellationToken);
        if (controlAccount is null || controlAccount.ClientId != clientId)
            return Results.NotFound();
        if (controlAccount.RequiredDimensions.Count == 0)
            return Unprocessable(
                $"Account {accountId} is not a control account requiring a dimension; subledger reconciliation does not apply. Use the account balance or journal instead.");
        if (!controlAccount.RequiredDimensions.Contains(dimension))
            return Unprocessable(
                $"Account {accountId} requires the '{string.Join(", ", controlAccount.RequiredDimensions)}' dimension(s), not '{dimension}'.");

        // Both sides folded the same way: the control balance includes every line on the account; the
        // subledger only the lines carrying the dimension. Their difference is the untagged remainder.
        IReadOnlyDictionary<Guid, decimal> balances =
            await ctx.Ledger.Journal.AggregateBalancesAsync(clientId, asOf, cancellationToken);
        decimal control = balances.GetValueOrDefault(accountId);

        IReadOnlyList<SubledgerBalance> subledger =
            await ctx.Ledger.Journal.AggregateSubledgerAsync(clientId, dimension, accountId, asOf, cancellationToken: cancellationToken);
        decimal subledgerTotal = subledger.Sum(s => s.Balance);

        decimal variance = control - subledgerTotal;
        return Results.Ok(new SubledgerReconciliationResponse(
            accountId, dimension, asOf, control, subledgerTotal, variance, variance == 0m));
    }

    private static async Task<IResult> GetBalanceSheet(
        Guid clientId, DateOnly? asOf, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // "As of now" defaults to today; future-dated entries are excluded, as a balance sheet expects.
        DateOnly asOfDate = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        BalanceSheet sheet = await ctx.Ledger.Statements.BalanceSheetAsync(clientId, asOfDate, cancellationToken);
        return Results.Ok(ToBalanceSheetResponse(sheet));
    }

    private static async Task<IResult> GetIncomeStatement(
        Guid clientId, DateOnly? from, DateOnly? to, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // An income statement is a flow over a window, so both bounds are required and must be ordered.
        if (from is not { } start || to is not { } end)
            return Unprocessable("An income statement requires both 'from' and 'to' dates.");
        if (start > end)
            return Unprocessable("'from' must not be after 'to'.");

        IncomeStatement statement = await ctx.Ledger.Statements.IncomeStatementAsync(clientId, start, end, cancellationToken);
        return Results.Ok(ToIncomeStatementResponse(statement));
    }

    private static async Task<IResult> GetCashFlow(
        Guid clientId, DateOnly? from, DateOnly? to, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // A cash-flow statement is a flow over a window, so both bounds are required and must be ordered.
        if (from is not { } start || to is not { } end)
            return Unprocessable("A cash-flow statement requires both 'from' and 'to' dates.");
        if (start > end)
            return Unprocessable("'from' must not be after 'to'.");

        CashFlowStatement statement = await ctx.Ledger.Statements.CashFlowStatementAsync(clientId, start, end, cancellationToken);
        return Results.Ok(ToCashFlowResponse(statement));
    }

    private static async Task<IResult> GetAccountBalance(
        Guid clientId, Guid accountId, DateOnly? asOf, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Absent asOf: the O(1) live projection. With asOf: a point-in-time fold from the journal, exactly as
        // GetTrialBalance does, then plucked for this account (0 when it has no activity through that date).
        decimal balance = asOf is { } asOfDate
            ? (await ctx.Ledger.Journal.AggregateBalancesAsync(clientId, asOfDate, cancellationToken)).GetValueOrDefault(accountId)
            : await ctx.Ledger.Projection.GetBalanceAsync(clientId, accountId, cancellationToken);

        Account? account = (await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken)).Find(accountId);
        return Results.Ok(new AccountBalanceResponse(accountId, balance, account?.Number, account?.Name));
    }

    private static async Task<IResult> GetClientAudit(
        Guid clientId, int? skip, int? limit, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        List<AuditRecordResponse> items = ToAuditResponses(
            await ctx.Ledger.Audit.GetForClientAsync(clientId, Page(skip), PageLimit(limit), cancellationToken));
        if (skip is not null || limit is not null)
        {
            long total = await ctx.Ledger.Audit.CountForClientAsync(clientId, cancellationToken);
            return Results.Ok(new PagedResponse<AuditRecordResponse>(items, total, Page(skip), PageLimit(limit)));
        }
        return Results.Ok(items);
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

        AuditChainVerification v = await ctx.Ledger.Audit.VerifyDetailedAsync(clientId, cancellationToken);
        return Results.Ok(new AuditVerifyResponse(v.Valid, v.RecordCount, v.HeadSequence, v.Failure?.ToString(), v.BrokenAtSequence));
    }

    // ---- Chart of accounts + year-end -------------------------------------------------------

    private static async Task<IResult> UpsertAccount(
        Guid clientId, Guid accountId, AccountRequest request, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.ManageAccounts, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Parse the wire enums up front so a bad Type/CashFlowActivity value returns an actionable,
        // structured 422 (field + bad value + valid values) instead of a raw Enum.Parse exception
        // message — parity with the entry-post path's TryMapEntry errors.
        Dictionary<string, string[]> parseErrors = [];
        if (!TryParseEnum(request.Type, "type", out AccountType accountType, out KeyValuePair<string, string[]>? typeErr))
            parseErrors.Add(typeErr!.Value.Key, typeErr.Value.Value);

        CashFlowActivity? cashFlow = null;
        if (request.CashFlowActivity is { } cfa)
        {
            if (TryParseEnum(cfa, "cashFlowActivity", out CashFlowActivity parsedCfa, out KeyValuePair<string, string[]>? cfaErr))
                cashFlow = parsedCfa;
            else
                parseErrors.Add(cfaErr!.Value.Key, cfaErr.Value.Value);
        }

        if (parseErrors.Count > 0) return ValidationProblem(parseErrors);

        Account account;
        try
        {
            account = MapAccount(clientId, accountId, request, accountType, cashFlow);
        }
        catch (ArgumentException ex) // unknown RequiredDimension
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

        // Routed through ChartService so the change is recorded on the tamper-evident chain (who, what,
        // before/after) atomically with the write — chart edits are control-relevant, not silent.
        await ctx.Ledger.Chart.UpsertAsync(account, ctx.Actor, cancellationToken);
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

    /// <summary>
    /// The distinct dimension keys declared across the client's chart (the union of every account's
    /// <see cref="Account.RequiredDimensions"/>), sorted. A discovery list for a caller building a
    /// dimension picker — no journal scan, since the vocabulary lives entirely in the chart.
    /// </summary>
    private static async Task<IResult> GetDimensions(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        List<string> dims = chart.Accounts
            .SelectMany(a => a.RequiredDimensions)
            .Distinct()
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(dims);
    }

    /// <summary>
    /// The distinct <see cref="JournalEntry.SourceType"/> values actually present in the client's
    /// journal, sorted — the discovery list a caller uses to offer source-type filters without
    /// hardcoding a vocabulary.
    /// </summary>
    private static async Task<IResult> GetSourceTypes(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        IReadOnlyList<string> types = await ctx.Ledger.Journal.DistinctSourceTypesAsync(clientId, cancellationToken);
        return Results.Ok(types);
    }

    private static async Task<IResult> CloseYear(
        Guid clientId, CloseYearRequest request, LedgerGateway gateway, ControlStore control,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Close, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Symmetric fiscal-year-end guard: close-year closes the fiscal year — refuse any other date.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is not null)
        {
            DateOnly fye = FiscalYear.EndDateFor(FiscalYear.MonthOf(client), request.FiscalYearEnd.Year);
            if (request.FiscalYearEnd != fye)
                return Results.Problem(
                    detail: $"{request.FiscalYearEnd:yyyy-MM-dd} is not this client's fiscal year-end ({fye:yyyy-MM-dd}). "
                          + "close-year closes the fiscal year; use the monthly close for an ordinary period.",
                    statusCode: StatusCodes.Status409Conflict);
        }

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        try
        {
            JournalEntry? closing = await ctx.Ledger.Service.CloseYearAsync(
                clientId, request.FiscalYearEnd, ctx.Actor, chart, cancellationToken);
            return Results.Ok(new CloseYearResponse(closing is null ? null : ToEntryResponse(closing)));
        }
        catch (PeriodCloseBlockedException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["blockers"] = ex.Blockers
                        .Select(b => new PendingEntryRef(b.Id, b.Reference, b.EffectiveDate, b.Type.ToString()))
                        .ToList(),
                });
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

    private static IResult ValidationProblem(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, detail: "One or more fields are invalid.",
            statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>Parse an enum case-insensitively, or produce a structured field error listing the valid
    /// values — so a bad wire value returns an actionable 422 instead of a raw Enum.Parse message.</summary>
    private static bool TryParseEnum<TEnum>(
        string? raw, string field, out TEnum value, out KeyValuePair<string, string[]>? error)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value))
        {
            error = null;
            return true;
        }
        string valid = string.Join(", ", Enum.GetNames<TEnum>());
        error = new KeyValuePair<string, string[]>(
            field, [$"'{raw}' is not a valid {typeof(TEnum).Name}. Valid: {valid}."]);
        return false;
    }

    private static EntryResponse ToEntryResponse(JournalEntry e) => new(
        e.Id, e.SequenceNumber, e.EffectiveDate,
        e.Type.ToString(), e.Status.ToString(), e.Posting.ToString(),
        e.Lines.Count, e.Supersedes, e.SupersededBy, e.ReversalOf, e.ReversedBy,
        e.Lines.Select(l => new EntryLineResponse(
            l.AccountId, l.Direction.ToString(), l.Amount, l.Dimensions, l.LineMemo)).ToList(),
        e.SourceRef, e.SourceType, e.Reference, e.Memo, e.Audit.ViaModule);

    private static AccountResponse ToAccountResponse(Account a) => new(
        a.Id, a.Number, a.Name, a.Type.ToString(), a.ParentId, a.Postable,
        a.RequiredDimension, a.RequiredDimensions.ToArray(), a.CashFlowActivity?.ToString(),
        a.IsRetainedEarnings, a.Active, a.NormalSide.ToString(), a.IsTemporary);

    private static Account MapAccount(
        Guid clientId, Guid accountId, AccountRequest request, AccountType type, CashFlowActivity? cashFlow) => new()
    {
        Id = accountId,
        ClientId = clientId,
        Number = request.Number,
        Name = request.Name,
        Type = type,
        ParentId = request.ParentId,
        Postable = request.Postable,
        RequiredDimensions = request.RequiredDimensions is { Count: > 0 } set
            ? set.Distinct().ToArray()
            : request.RequiredDimension is { } single ? [single] : [],
        CashFlowActivity = cashFlow,
        IsRetainedEarnings = request.IsRetainedEarnings,
        Active = request.Active,
    };

    private static List<AccountBalanceResponse> ToAccountBalances(IReadOnlyDictionary<Guid, decimal> balances) =>
        balances.Select(kv => new AccountBalanceResponse(kv.Key, kv.Value)).ToList();

    /// <summary>Same as <see cref="ToAccountBalances"/> but labeled with each account's number/name from the chart.</summary>
    private static List<AccountBalanceResponse> ToLabeledBalances(
        IReadOnlyDictionary<Guid, decimal> balances, ChartOfAccounts chart) =>
        balances.Select(kv =>
        {
            Account? a = chart.Find(kv.Key);
            return new AccountBalanceResponse(kv.Key, kv.Value, a?.Number, a?.Name);
        }).ToList();

    private static BalanceSheetResponse ToBalanceSheetResponse(BalanceSheet sheet) => new(
        sheet.AsOf,
        ToSectionResponse(sheet.Assets),
        ToSectionResponse(sheet.Liabilities),
        ToSectionResponse(sheet.Equity),
        sheet.TotalAssets,
        sheet.TotalLiabilitiesAndEquity,
        sheet.IsBalanced);

    private static IncomeStatementResponse ToIncomeStatementResponse(IncomeStatement statement) => new(
        statement.From,
        statement.To,
        ToSectionResponse(statement.Revenue),
        ToSectionResponse(statement.Expenses),
        statement.NetIncome);

    private static CashFlowStatementResponse ToCashFlowResponse(CashFlowStatement s) => new(
        s.From,
        s.To,
        s.NetIncome,
        ToSectionResponse(s.OperatingAdjustments),
        s.OperatingCash,
        ToSectionResponse(s.Investing),
        ToSectionResponse(s.Financing),
        s.NetChangeInCash,
        s.BeginningCash,
        s.EndingCash,
        s.TiesOut);

    private static StatementSectionResponse ToSectionResponse(StatementSection section) => new(
        section.Title,
        section.Lines.Select(l => new StatementLineResponse(l.AccountId, l.Number, l.Name, l.Amount)).ToList(),
        section.Total);

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

    /// <summary>
    /// Accumulates every field-level validation error across all lines rather than throwing on the
    /// first bad value. Returns false (and populates <paramref name="errors"/>) when any line has
    /// an invalid direction, the entry type is not postable, or the lines do not balance.
    /// Callers that do not need per-field error accumulation (e.g. revise) should continue using
    /// <see cref="MapEntry"/> directly.
    /// </summary>
    private static bool TryMapEntry(
        Guid clientId,
        PostEntryRequest request,
        Actor actor,
        string? viaModule,
        out JournalEntry? entry,
        out Dictionary<string, string[]> errors)
    {
        errors = [];

        // Guard: null or fewer than two lines can't balance and would throw ArgumentException in Create.
        // Reject early so the error lands in the structured errors map rather than becoming a 500.
        if (request.Lines is null or { Count: < 2 })
        {
            errors["lines"] = ["A journal entry needs at least two lines."];
            entry = null;
            return false;
        }

        // Pass 1: parse directions; collect all bad ones before bailing.
        List<Line> lines = [];
        for (int i = 0; i < request.Lines.Count; i++)
        {
            PostLineRequest l = request.Lines[i];
            string key = $"lines[{i}].direction";
            if (string.IsNullOrEmpty(l.Direction))
            {
                errors[key] = ["A direction is required; expected 'Debit' or 'Credit'."];
            }
            else if (!Enum.TryParse<Direction>(l.Direction, ignoreCase: true, out Direction dir))
            {
                errors[key] = [$"'{l.Direction}' is not a valid direction; expected 'Debit' or 'Credit'."];
            }
            else
            {
                lines.Add(new Line
                {
                    Id = Guid.NewGuid(),
                    AccountId = l.AccountId,
                    Direction = dir,
                    Amount = l.Amount,
                    Dimensions = l.Dimensions ?? ReadOnlyDictionary<string, Guid>.Empty,
                });
            }
        }

        // Pass 2: parse entry type (capture rather than throw).
        EntryType entryType = EntryType.Standard;
        try
        {
            entryType = ParsePostableType(request.Type);
        }
        catch (ArgumentException ex)
        {
            errors["type"] = [ex.Message];
        }

        if (errors.Count > 0)
        {
            entry = null;
            return false;
        }

        // Pass 3: balance check.
        try
        {
            entry = JournalEntry.Create(
                id: request.Id ?? Guid.NewGuid(),
                clientId: clientId,
                sequenceNumber: 0,
                effectiveDate: request.EffectiveDate,
                postedAt: DateTimeOffset.UtcNow,
                type: entryType,
                audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow, ViaModule = viaModule },
                lines: lines,
                sourceRef: request.SourceRef,
                sourceType: request.SourceType,
                reference: request.Reference,
                memo: request.Memo);
            return true;
        }
        catch (UnbalancedEntryException ex)
        {
            errors["balance"] = [$"The entry does not balance: debits minus credits = {ex.Imbalance}."];
            entry = null;
            return false;
        }
        catch (ArgumentException) // too-few-lines (belt-and-suspenders: null/count guard above catches most)
        {
            errors["lines"] = ["A journal entry needs at least two lines."];
            entry = null;
            return false;
        }
    }

    private static JournalEntry MapEntry(Guid clientId, PostEntryRequest request, Actor actor) =>
        JournalEntry.Create(
            id: request.Id ?? Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: 0, // engine-assigned at append
            effectiveDate: request.EffectiveDate,
            postedAt: DateTimeOffset.UtcNow,
            type: ParsePostableType(request.Type),
            audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines: MapLines(request.Lines),
            sourceRef: request.SourceRef,
            sourceType: request.SourceType,
            reference: request.Reference,
            memo: request.Memo);

    private static JournalEntry MapReplacement(Guid clientId, Guid originalId, ReviseRequest request, Actor actor) =>
        JournalEntry.Create(
            id: request.Id ?? Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: 0, // engine-assigned at append
            effectiveDate: request.EffectiveDate,
            postedAt: DateTimeOffset.UtcNow,
            type: ParsePostableType(request.Type),
            audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines: MapLines(request.Lines),
            supersedes: originalId,
            sourceRef: request.SourceRef,
            sourceType: request.SourceType,
            reference: request.Reference,
            memo: request.Memo);

    /// <summary>
    /// The entry type a caller may set when posting: Standard (default) or Adjusting. Opening, Closing,
    /// and Reversing are engine-generated through their own paths, so they are refused here.
    /// </summary>
    private static EntryType ParsePostableType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return EntryType.Standard;
        if (!Enum.TryParse(type, ignoreCase: true, out EntryType parsed) ||
            parsed is not (EntryType.Standard or EntryType.Adjusting))
            throw new ArgumentException($"EntryType must be 'Standard' or 'Adjusting'; '{type}' cannot be posted directly.");
        return parsed;
    }

    private static List<Line> MapLines(IReadOnlyList<PostLineRequest> lines) =>
        lines.Select(l => new Line
        {
            Id = Guid.NewGuid(),
            AccountId = l.AccountId,
            Direction = Enum.Parse<Direction>(l.Direction, ignoreCase: true),
            Amount = l.Amount,
            Dimensions = l.Dimensions ?? ReadOnlyDictionary<string, Guid>.Empty,
        }).ToList();

    private static List<Line> MapOpeningLines(IReadOnlyList<OpeningBalanceLine> balances) =>
        balances.Select(b => new Line
        {
            Id = Guid.NewGuid(),
            AccountId = b.AccountId,
            Direction = b.Balance >= 0m ? Direction.Debit : Direction.Credit,
            Amount = Math.Abs(b.Balance),
            Dimensions = b.Dimensions ?? ReadOnlyDictionary<string, Guid>.Empty,
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

            foreach (string dimension in account.RequiredDimensions)
                if (!line.Dimensions.ContainsKey(dimension))
                    errors.Add($"Account {account.Number} \"{account.Name}\" requires a {dimension} on the posting line.");
        }

        return errors.Count == 0 ? null : Unprocessable(string.Join(" ", errors));
    }

    /// <summary>
    /// Structured variant of <see cref="ChartViolationsAsync"/>: returns a
    /// <c>Dictionary&lt;string,string[]&gt;</c> keyed <c>lines[{i}].accountId</c> for each line
    /// that violates the chart. Returns an empty dictionary when the chart is unset or all lines
    /// conform. Callers use <see cref="ValidationProblem(IDictionary{string,string[]})"/> on a
    /// non-empty result.
    /// </summary>
    private static async Task<Dictionary<string, string[]>> ChartFieldViolationsAsync(
        MongoAccountStore accounts, Guid clientId, IReadOnlyList<Line> lines, CancellationToken cancellationToken)
    {
        ChartOfAccounts chart = await accounts.GetChartAsync(clientId, cancellationToken);
        if (chart.Accounts.Count == 0)
            return [];

        Dictionary<string, string[]> errors = [];
        for (int i = 0; i < lines.Count; i++)
        {
            Line line = lines[i];
            Account? account = chart.Find(line.AccountId);
            List<string> lineErrors = [];

            if (account is null)
            {
                lineErrors.Add($"Account {line.AccountId} is not in the chart of accounts.");
            }
            else
            {
                if (!account.Active)
                    lineErrors.Add($"Account {account.Number} \"{account.Name}\" is inactive.");
                else if (!account.Postable)
                    lineErrors.Add($"Account {account.Number} \"{account.Name}\" is a summary account and cannot be posted to.");

                foreach (string dimension in account.RequiredDimensions)
                    if (!line.Dimensions.ContainsKey(dimension))
                        lineErrors.Add($"Account {account.Number} \"{account.Name}\" requires a {dimension} on the posting line.");

                // Typo guard: a control account (one that declares required dimensions) must not carry an UNDECLARED
                // dimension key — a misspelled key ("Custommer") would otherwise be stored silently and the subledger
                // fold, which keys on the declared dimension, would never see it. Non-control accounts are untouched.
                if (account.RequiredDimensions.Count > 0)
                    foreach (string key in line.Dimensions.Keys)
                        if (!account.RequiredDimensions.Contains(key))
                            lineErrors.Add(
                                $"Account {account.Number} \"{account.Name}\" does not declare the dimension '{key}' "
                                + $"(expected: {string.Join(", ", account.RequiredDimensions)}).");
            }

            if (lineErrors.Count > 0)
                errors[$"lines[{i}].accountId"] = [.. lineErrors];
        }

        return errors;
    }
}
