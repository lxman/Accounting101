export interface StatementLine {
  accountId: string | null;
  number: string | null;
  name: string;
  amount: number;
}

export interface StatementSection {
  title: string;
  lines: StatementLine[];
  total: number;
}

export interface BalanceSheetResponse {
  asOf: string;
  assets: StatementSection;
  liabilities: StatementSection;
  equity: StatementSection;
  totalAssets: number;
  totalLiabilitiesAndEquity: number;
  isBalanced: boolean;
}

export interface IncomeStatementResponse {
  from: string;
  to: string;
  revenue: StatementSection;
  expenses: StatementSection;
  netIncome: number;
}
