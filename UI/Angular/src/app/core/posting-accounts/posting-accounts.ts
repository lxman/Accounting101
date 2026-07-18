import { AccountResponse } from '../accounts/account';

export interface PostingAccountSlot {
  moduleKey: string;
  slotKey: string;
  label: string;
  expectedType: string;
  requiredDimensions: string[];
  currentAccountId: string | null;
}

export interface PostingAccounts {
  slots: PostingAccountSlot[];
}

// Reuse the real chart-of-accounts wire shape (core/accounts/account.ts) rather than
// redefining a parallel DTO — GET /clients/{id}/accounts returns AccountResponse[].
export type ChartAccount = AccountResponse;

export interface RevenueCategories {
  moduleKey: string;
  categories: Record<string, string>;
  source: 'stored' | 'config';
}
