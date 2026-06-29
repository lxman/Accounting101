export interface TrialBalanceRow { accountId: string; balance: number; }
export interface TrialBalanceResponse { asOf: string | null; accounts: TrialBalanceRow[]; }
