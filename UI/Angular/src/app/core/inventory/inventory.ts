export type ItemStatus = 'Active' | 'Inactive';
export type MovementType = 'Receipt' | 'Issue' | 'Adjustment';
export type MovementStatus = 'Posted' | 'Void';

export interface Item {
  id: string; sku: string; name: string; description: string | null;
  unitOfMeasure: string; status: ItemStatus; onHandQuantity: number; totalValue: number;
}
export interface ItemView { item: Item; averageUnitCost: number; }
export interface StockMovement {
  id: string; number: string | null; itemId: string; type: MovementType;
  effectiveDate: string; memo: string | null; quantity: number;
  appliedUnitCost: number; extendedCost: number;
  resultingOnHand: number; resultingTotalValue: number; status: MovementStatus;
}
export interface StockMovementView { movement: StockMovement; }
export interface SaveItemRequest { sku: string; name: string; description: string | null; unitOfMeasure: string; }
export interface RecordMovementRequest {
  itemId: string; type: MovementType; quantity: number;
  unitCost: number | null; effectiveDate: string; memo: string | null;
}

export interface InventoryListQuery { skip: number; limit: number; order?: 'asc' | 'desc'; includeInactive?: boolean; }
export interface MovementListQuery { skip: number; limit: number; order?: 'asc' | 'desc'; includeVoided?: boolean; }
