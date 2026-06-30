export type BillStatus = 'Draft' | 'Entered' | 'Void';
export type SettlementStatus = 'Open' | 'PartiallyPaid' | 'Paid';
export type SettlementFilter = 'open' | 'paid';

export interface Vendor { id: string; name: string; email: string | null; }

export interface BillLine { description: string; amount: number; expenseAccountId: string; }

export interface Bill {
  id: string; vendorId: string; number: string | null;
  billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null;
  status: BillStatus; lines: BillLine[];
}

export interface BillView { bill: Bill; openBalance: number; settlementStatus: SettlementStatus; }

export interface DraftBillRequest {
  vendorId: string; billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null; lines: BillLine[];
}

export interface VoidBillRequest { reason: string | null; }

export interface BillListQuery {
  vendorId: string; settlement?: SettlementFilter; skip: number; limit: number; order?: 'asc' | 'desc';
}

/** Bill total — sum of line amounts (bills carry no tax). */
export const billTotal = (lines: readonly Pick<BillLine, 'amount'>[]): number =>
  lines.reduce((s, l) => s + l.amount, 0);
