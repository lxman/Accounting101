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

export interface PaymentAllocation { targetId: string; amount: number; }

export interface BillPayment {
  id: string; vendorId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[]; voided: boolean;
}

export interface RecordBillPaymentRequest {
  vendorId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[];
}

/** One open bill the user can apply cash to in the payment editor. */
export interface AllocRow {
  billId: string; number: string | null; billDate: string; openBalance: number; allocation: number;
}

/** Distribute `amount` across rows in their given (oldest-first) order, each capped at its open
 *  balance. Returns new rows; any remainder beyond the rows' open balances is left unallocated
 *  (→ vendor credit). Mirror of the receivables helper. */
export function autoAllocate(amount: number, rows: readonly AllocRow[]): AllocRow[] {
  let remaining = Math.max(0, amount);
  return rows.map(r => {
    const take = Math.min(r.openBalance, remaining);
    remaining = Math.round((remaining - take) * 100) / 100;
    return { ...r, allocation: Math.round(take * 100) / 100 };
  });
}

export interface VendorCreditApplication {
  id: string; vendorId: string; date: string; allocations: PaymentAllocation[]; voided: boolean;
}

export interface ApplyVendorCreditRequest {
  vendorId: string; date: string; allocations: PaymentAllocation[];
}

export interface AgingBuckets { current: number; d1To30: number; d31To60: number; d61To90: number; d90Plus: number; }
export interface OpenBillLine { billId: string; number: string | null; billDate: string; dueDate: string | null; openBalance: number; daysOverdue: number; }
export interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; }
export interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; }
export interface VendorAccountView {
  vendor: Vendor; apBalance: number; creditBalance: number; aging: AgingBuckets;
  openBills: OpenBillLine[]; statementLines: StatementLine[]; creditLines: CreditActivityLine[];
}
