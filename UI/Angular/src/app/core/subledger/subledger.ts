export interface SubledgerReconciliationLine {
  account: string; number: string | null; name: string | null; dimension: string;
  controlBalance: number; subledgerTotal: number; variance: number; tiesOut: boolean;
}
export interface SubledgerReconciliationsResponse { asOf: string | null; lines: SubledgerReconciliationLine[]; }

export interface SubledgerLineResponse { accountId: string; dimensionValue: string; balance: number; number: string | null; name: string | null; }
export interface SubledgerResponse { dimension: string; asOf: string | null; lines: SubledgerLineResponse[]; }
