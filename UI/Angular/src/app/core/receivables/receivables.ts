export type InvoiceStatus = 'Draft' | 'Issued' | 'Void';
export type SettlementStatus = 'Open' | 'PartiallyPaid' | 'Paid';
export type SettlementFilter = 'open' | 'paid';

export interface Customer { id: string; name: string; email: string | null; }

export interface InvoiceLine {
  description: string; quantity: number; unitPrice: number;
  taxable: boolean; revenueCategory: string | null;
}
export interface Invoice {
  id: string; customerId: string; number: string | null;
  issueDate: string; dueDate: string | null; status: InvoiceStatus;
  taxRate: number; memo: string | null; lines: InvoiceLine[];
}
export interface InvoiceView { invoice: Invoice; openBalance: number; settlementStatus: SettlementStatus; }

export interface DraftInvoiceRequest {
  customerId: string; lines: InvoiceLine[]; taxRate: number;
  issueDate: string; dueDate: string | null; memo: string | null;
}
export interface VoidInvoiceRequest { reason: string | null; }

export interface InvoiceListQuery {
  customerId: string; settlement?: SettlementFilter; skip: number; limit: number; order?: 'asc' | 'desc';
}

/** Pure money math mirroring the backend Invoice computed fields. */
export const lineAmount = (l: Pick<InvoiceLine, 'quantity' | 'unitPrice'>): number => l.quantity * l.unitPrice;
export function invoiceTotals(lines: readonly InvoiceLine[], taxRate: number): { subtotal: number; tax: number; total: number } {
  const subtotal = lines.reduce((s, l) => s + lineAmount(l), 0);
  const taxableBase = lines.filter(l => l.taxable).reduce((s, l) => s + lineAmount(l), 0);
  const tax = Math.round(taxRate * taxableBase * 100) / 100;   // 2dp, half-away-from-zero for non-negative inputs
  return { subtotal, tax, total: subtotal + tax };
}

export interface PaymentAllocation { targetId: string; amount: number; }
export interface Payment {
  id: string; customerId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[]; voided: boolean;
}
export interface RecordPaymentRequest {
  customerId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[];
}

export type CreditType = 'credit-note' | 'write-off' | 'credit-application';
export interface CreditDocument {
  type: CreditType; id: string; customerId: string; date: string;
  amount: number; memo: string | null; allocations: PaymentAllocation[]; voided: boolean;
}
export interface CreditNoteRequest { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }
export interface WriteOffRequest   { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }
export interface CreditApplyRequest { customerId: string; date: string; allocations: PaymentAllocation[]; }

export interface Refund { id: string; customerId: string; date: string; amount: number; memo: string | null; voided: boolean; }
export interface RefundRequest { customerId: string; date: string; amount: number; memo: string | null; }

/** A payment-allocation editor row: one open invoice the user can apply cash to. */
export interface AllocRow {
  invoiceId: string; number: string | null; issueDate: string; openBalance: number; allocation: number;
}

/** Distribute `amount` across rows in their given (oldest-first) order, each capped at its open balance.
 *  Returns new rows; any remainder beyond the rows' open balances is left unallocated (→ customer credit). */
export function autoAllocate(amount: number, rows: readonly AllocRow[]): AllocRow[] {
  let remaining = Math.max(0, amount);
  return rows.map(r => {
    const take = Math.min(r.openBalance, remaining);
    remaining = Math.round((remaining - take) * 100) / 100;
    return { ...r, allocation: Math.round(take * 100) / 100 };
  });
}

export interface AgingBuckets { current: number; d1to30: number; d31to60: number; d61to90: number; d90plus: number; }
export interface OpenInvoiceLine { invoiceId: string; number: string | null; issueDate: string; dueDate: string | null; openBalance: number; daysOverdue: number; }
export interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; }
export interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; }
export interface CustomerAccountView {
  customer: Customer; arBalance: number; creditBalance: number; aging: AgingBuckets;
  openInvoices: OpenInvoiceLine[]; statementLines: StatementLine[]; creditLines: CreditActivityLine[];
}
