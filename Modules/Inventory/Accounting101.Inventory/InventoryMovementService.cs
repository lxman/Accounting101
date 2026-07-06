using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>Domain input for recording a stock movement. <see cref="EffectiveDate"/> is required — the
/// clerk always dates a movement (mirrors how FA runs take a period and cash vouchers take a date); the
/// engine forbids wall-clock reads in module code, so there is no "default to today" fallback.</summary>
public sealed record RecordMovement(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo);

/// <summary>Records a stock movement: validates shape, resolves and locks the item, re-blends its
/// valuation via <see cref="InventoryValuation"/>, persists the numbered movement, applies the new
/// valuation to the item, and posts one balanced PendingApproval entry via <see cref="InventoryPosting"/>.
/// This is the module's first GL-posting service.</summary>
public sealed class InventoryMovementService(
    IItemStore items, IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger)
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
        Valuation current = new(item.OnHandQuantity, item.TotalValue);
        MovementEffect effect = request.Type switch
        {
            MovementType.Receipt    => InventoryValuation.Receipt(current, request.Quantity, request.UnitCost!.Value),
            MovementType.Issue      => InventoryValuation.Issue(current, request.Quantity),
            MovementType.Adjustment => InventoryValuation.Adjustment(current, request.Quantity, request.UnitCost),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        // 4. Resolve accounts BEFORE persistence — config error must fail before side effects.
        InventoryPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        // 5. Persist the numbered movement with its snapshot.
        StockMovement movement = await movements.RecordAsync(clientId, new StockMovementBody(
            request.ItemId, request.Type, request.EffectiveDate, request.Memo,
            request.Quantity, effect.AppliedUnitCost, effect.ExtendedCost,
            effect.ResultingOnHand, effect.ResultingTotalValue), ct);

        // 6. Apply the new valuation to the item.
        await items.SetValuationAsync(clientId, request.ItemId, effect.ResultingOnHand, effect.ResultingTotalValue, ct);

        // 7. Compose + post one PendingApproval entry.
        PostEntryRequest entry = InventoryPosting.Compose(
            request.Type, request.Quantity, movement.Id, effect.ExtendedCost, request.EffectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return movement;
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
