export type Direction = 'Debit' | 'Credit';
export type Posting = 'PendingApproval' | 'Posted';

export interface EntryLineResponse {
  accountId: string;
  direction: Direction;
  amount: number;
  dimensions: Record<string, string>;
  lineMemo: string | null;
}

export interface EntryResponse {
  id: string;
  sequenceNumber: number;
  effectiveDate: string;
  type: string;
  status: string;
  posting: Posting;
  lineCount: number;
  supersedes: string | null;
  supersededBy: string | null;
  reversalOf: string | null;
  reversedBy: string | null;
  lines: EntryLineResponse[];
  sourceRef: string | null;
  sourceType: string | null;
  reference: string | null;
  memo: string | null;
  viaModule: string | null;
}
