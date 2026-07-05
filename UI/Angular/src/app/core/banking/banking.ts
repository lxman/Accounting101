export type CashStatus = 'Posted' | 'Void';
export type AdjustmentKind = 'Charge' | 'Credit';
export type ReconciliationStatus = 'InProgress' | 'Completed';
export type BankStatementStatus = 'Posted' | 'Void';

export const adjustmentKindLabel = (k: AdjustmentKind): string =>
  k === 'Charge' ? 'Bank charge' : 'Bank interest';

// ── Cash vouchers ────────────────────────────────────────────────────────────
export interface CashLine { accountId: string; amount: number; }
export interface CashDisbursement {
  id: string; number: string | null; lines: CashLine[];
  date: string; reference: string | null; memo: string | null; status: CashStatus;
}
export type CashDeposit = CashDisbursement;                 // identical shape, different endpoint
export interface CashDisbursementView { disbursement: CashDisbursement; }
export interface CashDepositView { deposit: CashDeposit; }
export type CashKind = 'disbursement' | 'deposit';
/** A row in the combined cash list — normalized across both kinds. */
export interface CashVoucherRow {
  id: string; kind: CashKind; number: string | null; date: string;
  amount: number; memo: string | null; status: CashStatus;
}
/** A view of either a disbursement or deposit, normalized for detail screens. */
export interface CashVoucherView {
  id: string; kind: CashKind; number: string | null; lines: CashLine[];
  date: string; reference: string | null; memo: string | null; status: CashStatus;
}

export interface RecordCashVoucherRequest {
  lines: CashLine[]; date: string; reference?: string | null; memo?: string | null;
}

// ── Bank statements ──────────────────────────────────────────────────────────
export interface BankStatementLine { date: string; amount: number; description: string; externalRef: string | null; }
export interface BankStatement {
  id: string; number: string | null; cashAccountId: string; statementDate: string;
  openingBalance: number; closingBalance: number; lines: BankStatementLine[]; status: BankStatementStatus;
}
export interface RecordBankStatementRequest {
  cashAccountId: string; statementDate: string; openingBalance: number; closingBalance: number;
  lines: BankStatementLine[];
}

// ── Import (parse-to-preview) ────────────────────────────────────────────────
export type InterchangeFormat = 'Csv' | 'Ofx';
export interface ColumnRef { index?: number | null; header?: string | null; }
export interface CsvMapping {
  date: ColumnRef; amount?: ColumnRef | null; debit?: ColumnRef | null; credit?: ColumnRef | null;
  description: ColumnRef; reference?: ColumnRef | null; dateFormat?: string | null;
  hasHeader: boolean; delimiter?: string | null;
  status?: ColumnRef | null; excludeStatuses?: string[] | null;
}
export interface StatementPreview {
  lines: BankStatementLine[]; detectedOpeningBalance: number | null; detectedClosingBalance: number | null;
  statementDate: string | null; accountHint: string | null;
}
export interface ImportPreviewResponse { statements: StatementPreview[]; warnings: string[]; }
/** The form state driving the statement-import wizard (not a wire type). */
export interface StatementImportForm {
  format: InterchangeFormat; cashAccountId: string | null;
  csvMapping: CsvMapping | null; rawContent: string | null;
}

// ── Reconciliation ───────────────────────────────────────────────────────────
export interface ReconciliationRef {
  id: string; number: string | null; cashAccountId: string; bankStatementId: string;
  statementDate: string; status: ReconciliationStatus; clearedEntryIds: string[];
}
export interface WorksheetEntry {
  entryId: string; date: string; reference: string | null; sourceType: string | null;
  cashEffect: number; cleared: boolean;
}
export interface ReconciliationWorksheet {
  reconciliation: ReconciliationRef; statement: BankStatement; entries: WorksheetEntry[];
  bookBalance: number; clearedTotal: number; reconciledDifference: number; balanced: boolean;
}
export interface MatchableEntry { entryId: string; date: string; cashEffect: number; }
/**
 * NOTE: field names verified against the C# record in
 * Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AutoMatcher.cs:
 *   AutoMatch(int StatementLineIndex, decimal Amount, Guid EntryId, DateOnly LineDate, DateOnly EntryDate, int DaysApart)
 * — camelCased by System.Text.Json Web defaults.
 */
export interface AutoMatch {
  statementLineIndex: number; amount: number; entryId: string;
  lineDate: string; entryDate: string; daysApart: number;
}
export interface UnmatchedLine { statementLineIndex: number; date: string; amount: number; description: string; }
export interface AutoMatchProposal {
  matches: AutoMatch[]; unmatchedStatementLines: UnmatchedLine[];
  unmatchedEntries: MatchableEntry[]; matchedEntryIds: string[];
}

// ── Adjustments ──────────────────────────────────────────────────────────────
export interface BankAdjustment {
  id: string; number: string | null; reconciliationId: string; cashAccountId: string;
  offsetAccountId: string; kind: AdjustmentKind; amount: number; date: string;
  memo: string | null; status: CashStatus;
}
export interface RecordAdjustmentRequest {
  offsetAccountId: string; amount: number; kind: AdjustmentKind; date?: string | null; memo?: string | null;
}

export interface BankingListQuery { skip: number; limit: number; order?: 'asc' | 'desc'; }
