export type PayrollRunStatus = 'Posted' | 'Void';
export type TaxRemittanceStatus = 'Posted' | 'Void';

export interface PayrollRun {
  id: string;
  number: string | null;
  gross: number;
  employeeFica: number;
  employerFica: number;
  deductions: number;
  incomeTaxWithheld: number;
  payDate: string;
  memo: string | null;
  status: PayrollRunStatus;
}

export interface TaxRemittance {
  id: string;
  number: string | null;
  withholdingsAmount: number;
  taxesAmount: number;
  payDate: string;
  memo: string | null;
  status: TaxRemittanceStatus;
}

export interface PayrollRunView { run: PayrollRun; }
export interface TaxRemittanceView { remittance: TaxRemittance; }

export interface RecordPayrollRunRequest {
  gross: number;
  employeeFica: number;
  employerFica: number;
  deductions: number;
  incomeTaxWithheld: number;
  payDate: string;
  memo: string | null;
}

export interface RecordTaxRemittanceRequest {
  withholdingsAmount: number;
  taxesAmount: number;
  payDate: string;
  memo: string | null;
}

export interface PayrollListQuery {
  skip: number;
  limit: number;
  order?: 'asc' | 'desc';
  includeVoided?: boolean;
}

/** Net pay a run disburses = gross − employee FICA − income tax withheld − deductions. Not stored. */
export const netPay = (r: Pick<PayrollRun, 'gross' | 'employeeFica' | 'incomeTaxWithheld' | 'deductions'>): number =>
  Math.round((r.gross - r.employeeFica - r.incomeTaxWithheld - r.deductions) * 100) / 100;

/** Total cash a remittance pays = withholdings + taxes. Not stored. */
export const remittanceTotal = (r: Pick<TaxRemittance, 'withholdingsAmount' | 'taxesAmount'>): number =>
  Math.round((r.withholdingsAmount + r.taxesAmount) * 100) / 100;
