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
