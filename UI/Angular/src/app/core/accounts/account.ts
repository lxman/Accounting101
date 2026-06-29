export type AccountType = 'Asset' | 'Liability' | 'Equity' | 'Revenue' | 'Expense';
export interface AccountResponse {
  id: string; number: string; name: string; type: AccountType; parentId: string | null;
  postable: boolean; requiredDimension: string | null; cashFlowActivity: string | null;
  isRetainedEarnings: boolean; active: boolean; normalSide: 'Debit' | 'Credit'; isTemporary: boolean;
}

export interface AccountUpsert {
  id: string; number: string; name: string; type: AccountType;
  parentId: string | null; postable: boolean; requiredDimension: string | null;
  cashFlowActivity: string | null; isRetainedEarnings: boolean; active: boolean;
}
