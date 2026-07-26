using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>Domain input for recording a stock movement. <see cref="EffectiveDate"/> is required — the
/// clerk always dates a movement (mirrors how FA runs take a period and cash vouchers take a date); the
/// engine forbids wall-clock reads in module code, so there is no "default to today" fallback.
/// <see cref="Id"/> is the optional caller-supplied retry key that becomes the movement's id.</summary>
public sealed record RecordMovement(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo,
    Guid? Id = null);

/// <summary>Records a stock movement: validates shape, resolves the item, computes the costing effect
/// from the folded (pending-inclusive) valuation via <see cref="InventoryValuation"/>, persists the
/// numbered movement, and posts one balanced PendingApproval entry via <see cref="InventoryPosting"/>.
/// On-hand/value are never stamped onto the item — they are derived on read from the ledger fold +
/// movement projection. This is the module's first GL-posting service.</summary>
public sealed class InventoryMovementService(
    IItemStore items, IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger,
    ItemValuationService valuation)
{
    /// <summary>Records a movement, or REPLAYS one when the caller supplies a retry key that is already
    /// taken. The replay branch is wider here than on any other retry-key endpoint: this path derives what
    /// it posts from mutable state (the pending-inclusive fold), so re-running it on a replay would
    /// re-apply the movement's own effect to on-hand and value — a double-count the ledger's
    /// divergent-content 422 could NOT catch, because the composed entry would be identical.
    ///
    /// KNOWN NARROWING of the divergent-content 422 versus every other retry-key endpoint. A Receipt or an
    /// overage Adjustment costs at the unit cost ON THE REQUEST, so its extended cost is recomputed on a
    /// replay and a retry that changed the quantity or the unit cost still reaches the entry and still
    /// 422s. An Issue or a shrinkage Adjustment costs at the FOLDED weighted average, so its extended cost
    /// is frozen from the stored movement (see <see cref="CostsFromTheRequest"/>) — for those two, a retry
    /// under the same key with a different quantity composes a byte-identical entry, is NOT detected, and
    /// returns the ORIGINAL movement with 200. Date, movement type and item still divergence-check on
    /// every type.</summary>
    public async Task<(StockMovement Movement, bool Replayed)> RecordAsync(
        Guid clientId, RecordMovement request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Shape validation (→ 422). Direction/cost rules per movement type. Pure — it reads only the
        // request, so it guards BOTH branches: a replay carrying a malformed request is still a 422.
        ValidateShape(request);

        Guid id = request.Id ?? Guid.NewGuid();
        StockMovement? known = request.Id is not null ? await movements.GetAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        // 2. The posting accounts are needed to compose the entry on EVERY call, replay or not — data
        // acquisition, not a state-dependent guard (the same role this fetch plays in Cash/Payroll/
        // Reconciliation). Resolving it here still honours the original ordering promise — a config error
        // fails before any side effect — and now does so a little earlier than before.
        InventoryPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        // Assigned on both branches below — never defaulted, so a later refactor cannot silently feed a
        // placeholder cost into Compose (which would surface as an opaque 422).
        StockMovement movement;
        decimal extendedCost;

        if (!replayed)
        {
            // 3. Resolve item; must exist and be active. State-dependent — an item can be deactivated
            // between the original call and a later replay, and a replay must not be refused for that.
            Item item = await items.GetAsync(clientId, request.ItemId, ct)
                ?? throw new KeyNotFoundException($"Item {request.ItemId} not found.");
            if (item.Status != ItemStatus.Active)
                throw new InvalidOperationException("Item is inactive; reactivate it before recording movements.");

            // 4. Compute the effect (may throw InvalidOperationException for block-negative → 409).
            // Current valuation is the pending-inclusive fold (writes see pending claims), NOT the stored item.
            // State-dependent in the strongest sense: after the original call landed, this fold ALREADY
            // contains this movement.
            ItemValuation folded = await valuation.GetAsync(clientId, request.ItemId, includePending: true, ct);
            Valuation current = new(folded.OnHand, folded.TotalValue);
            MovementEffect effect = request.Type switch
            {
                MovementType.Receipt    => InventoryValuation.Receipt(current, request.Quantity, request.UnitCost!.Value),
                MovementType.Issue      => InventoryValuation.Issue(current, request.Quantity),
                MovementType.Adjustment => InventoryValuation.Adjustment(current, request.Quantity, request.UnitCost),
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };

            // Reject a non-positive extended cost BEFORE any side effect — otherwise the movement would be
            // persisted before InventoryPosting rejects it, stranding an entry-less movement. Derived from
            // the fold above, so it is state-dependent too and belongs on this branch.
            if (effect.ExtendedCost <= 0m)
                throw new ArgumentException("Movement extended cost must be positive.");

            extendedCost = effect.ExtendedCost;

            // 5. Persist the numbered movement under the caller's id. No valuation snapshot is stored —
            // on-hand and value are derived on read from the ledger fold + movement projection.
            //
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            //
            // SCOPE, deliberately partial. This fixes the STATUS CODE for every movement type, and that
            // is the whole fix for Receipt and overage Adjustment, whose extendedCost is Round(qty x
            // unitCost) — pure in the request. It does NOT fix Issue or shrinkage Adjustment: their cost
            // comes from a pending-inclusive weighted-average fold that the race winner has already moved,
            // and it is computed ABOVE this line, before the store call — so a loser can still get a
            // ledger 422, block-negative 409, or the extendedCost <= 0 guard for a movement that actually
            // landed. That self-heals (the next retry's probe finds the movement and freezes the cost from
            // the stored document) and is the subject of its own spec.
            (StockMovement Movement, bool Created) result = await movements.RecordAsync(clientId, id, new StockMovementBody(
                request.ItemId, request.Type, request.EffectiveDate, request.Memo,
                request.Quantity, effect.AppliedUnitCost, effect.ExtendedCost), ct);
            movement = result.Movement;
            replayed = !result.Created;
        }
        else
        {
            movement = known!;

            // 6. The extended cost is the ONLY part of the entry a replay cannot always take from the
            // request. Where it costs from the request (Receipt, overage Adjustment) recompute it, so a
            // retry that changed the quantity or the unit cost still diverges and still 422s. Where it
            // costs from the fold (Issue, shrinkage Adjustment) freeze it from the stored movement: that
            // fold now already contains this movement, so recomputing would yield a different cost, a zero
            // cost (issue-to-zero → 422), or a spurious block-negative 409 on a legitimate retry.
            extendedCost = CostsFromTheRequest(request)
                ? RequestExtendedCost(request)
                : movement.ExtendedCost;

            // Same rejection the create branch makes at step 4, for the recomputed arm: a retry re-sent
            // with a zero unit cost must be the module's own 422, not an opaque throw out of Compose.
            if (extendedCost <= 0m)
                throw new ArgumentException("Movement extended cost must be positive.");
        }

        // 7. Compose from the request and post one PendingApproval entry on BOTH branches — the ledger
        //    arbitrates a replay versus a key reused for different content, and a retry repairs a post that
        //    never landed. The entry's id is derived from the movement id, so a replay is idempotent there.
        PostEntryRequest entry = InventoryPosting.Compose(
            request.Type, request.Quantity, id, request.ItemId, extendedCost, request.EffectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return (movement, replayed);
    }

    /// <summary>True when this movement's extended cost is a pure function of the REQUEST — a Receipt or an
    /// overage Adjustment, both of which cost at <c>Round(quantity × unitCost)</c> and never read the
    /// current valuation (<see cref="InventoryValuation.Receipt"/> and the positive arm of
    /// <see cref="InventoryValuation.Adjustment"/>). False for an Issue or a shrinkage Adjustment, which
    /// cost at the folded weighted average and can also block-negative off it. This is exactly the line
    /// between "safe to recompute on a replay" and "must be frozen from the stored movement".</summary>
    private static bool CostsFromTheRequest(RecordMovement r) =>
        r.Type is MovementType.Receipt || (r.Type is MovementType.Adjustment && r.Quantity > 0m);

    /// <summary>The request-derived extended cost, computed through the SAME
    /// <see cref="InventoryValuation"/> arms the create branch uses so the rounding cannot drift apart.
    /// The zero valuation passed as <c>current</c> is inert: neither arm reads it to produce
    /// <see cref="MovementEffect.ExtendedCost"/>. Only ever called when
    /// <see cref="CostsFromTheRequest"/> holds, so the unit cost is present (ValidateShape guarantees it).</summary>
    private static decimal RequestExtendedCost(RecordMovement r) =>
        (r.Type is MovementType.Receipt
            ? InventoryValuation.Receipt(new Valuation(0m, 0m), r.Quantity, r.UnitCost!.Value)
            : InventoryValuation.Adjustment(new Valuation(0m, 0m), r.Quantity, r.UnitCost)).ExtendedCost;

    /// <summary>Voids the most-recent stock movement for its item — LIFO enforced: only the latest
    /// non-void movement for the item may be voided, never an earlier one while a later one still stands.
    /// Reverses the movement's spawned entry if posted (or withdraws it if still pending; tolerates a
    /// stranded post with no entry at all), then flips the movement's Status to Void. There is NO manual
    /// valuation restore: the reversed/withdrawn entry and the now-void movement drop straight out of the
    /// ledger fold + quantity projection, so the item's on-hand/value roll back on the next read. Mirrors
    /// FixedAssetsRunService.VoidRunAsync.</summary>
    public async Task<StockMovement> VoidAsync(Guid clientId, Guid movementId, string? reason, CancellationToken ct = default)
    {
        StockMovement movement = await movements.GetAsync(clientId, movementId, ct)
            ?? throw new KeyNotFoundException($"Stock movement {movementId} not found.");
        if (movement.Status != MovementStatus.Posted)
            throw new InvalidOperationException($"Only a posted movement can be voided; {movementId} is {movement.Status}.");

        // LIFO — only the most recent non-voided movement FOR THIS ITEM may be voided.
        StockMovement? latest = await movements.GetLatestForItemAsync(clientId, movement.ItemId, ct);
        if (latest is null || latest.Id != movement.Id)
            throw new InvalidOperationException("Only the most recent movement for this item can be voided.");

        // Reverse the posted entry (or withdraw it if still pending). Tolerate a missing entry (stranded post).
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, movementId, ct);
        EntryResponse? entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        if (entry is not null)
        {
            if (entry.Posting == "Posted")
                await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(movement.EffectiveDate, reason ?? $"Voided movement {movementId}"), ct);
            else
                await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided movement {movementId}"), ct);
        }

        await movements.VoidAsync(clientId, movementId, ct);
        return (await movements.GetAsync(clientId, movementId, ct))!;
    }

    private static void ValidateShape(RecordMovement r)
    {
        switch (r.Type)
        {
            case MovementType.Receipt:
                if (r.Quantity <= 0m) throw new ArgumentException("Receipt quantity must be positive.");
                if (r.UnitCost is not { } c || c < 0m) throw new ArgumentException("Receipt requires a non-negative unit cost.");
                break;
            case MovementType.Issue:
                if (r.Quantity <= 0m) throw new ArgumentException("Issue quantity must be positive.");
                break;
            case MovementType.Adjustment:
                if (r.Quantity == 0m) throw new ArgumentException("Adjustment quantity must be non-zero.");
                if (r.Quantity > 0m && (r.UnitCost is not { } uc || uc < 0m))
                    throw new ArgumentException("An increase adjustment requires a non-negative unit cost.");
                break;
        }
    }
}
