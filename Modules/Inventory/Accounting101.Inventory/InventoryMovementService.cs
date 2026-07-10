using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>Domain input for recording a stock movement. <see cref="EffectiveDate"/> is required — the
/// clerk always dates a movement (mirrors how FA runs take a period and cash vouchers take a date); the
/// engine forbids wall-clock reads in module code, so there is no "default to today" fallback.</summary>
public sealed record RecordMovement(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo);

/// <summary>Records a stock movement: validates shape, resolves the item, computes the costing effect
/// from the folded (pending-inclusive) valuation via <see cref="InventoryValuation"/>, persists the
/// numbered movement, and posts one balanced PendingApproval entry via <see cref="InventoryPosting"/>.
/// On-hand/value are never stamped onto the item — they are derived on read from the ledger fold +
/// movement projection. This is the module's first GL-posting service.</summary>
public sealed class InventoryMovementService(
    IItemStore items, IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger,
    ItemValuationService valuation)
{
    public async Task<StockMovement> RecordAsync(Guid clientId, RecordMovement request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Shape validation (→ 422). Direction/cost rules per movement type.
        ValidateShape(request);

        // 2. Resolve item; must exist and be active.
        Item item = await items.GetAsync(clientId, request.ItemId, ct)
            ?? throw new KeyNotFoundException($"Item {request.ItemId} not found.");
        if (item.Status != ItemStatus.Active)
            throw new InvalidOperationException("Item is inactive; reactivate it before recording movements.");

        // 3. Compute the effect (may throw InvalidOperationException for block-negative → 409).
        // Current valuation is the pending-inclusive fold (writes see pending claims), NOT the stored item.
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
        // persisted and the item mutated before InventoryPosting rejects it, stranding an entry-less movement.
        if (effect.ExtendedCost <= 0m)
            throw new ArgumentException("Movement extended cost must be positive.");

        // 4. Resolve accounts BEFORE persistence — config error must fail before side effects.
        InventoryPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        // 5. Persist the numbered movement. No valuation snapshot is stored — on-hand and value are
        // derived on read from the ledger fold + movement projection.
        StockMovement movement = await movements.RecordAsync(clientId, new StockMovementBody(
            request.ItemId, request.Type, request.EffectiveDate, request.Memo,
            request.Quantity, effect.AppliedUnitCost, effect.ExtendedCost), ct);

        // 6. Compose + post one PendingApproval entry.
        PostEntryRequest entry = InventoryPosting.Compose(
            request.Type, request.Quantity, movement.Id, request.ItemId, effect.ExtendedCost, request.EffectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return movement;
    }

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
