export interface PeriodStatus { closedThrough: string | null; fiscalYearEndMonth: number; }
export interface PendingEntryRef { entryId: string; reference: string | null; effectiveDate: string; type: string; }
export interface CloseResponse { asOf: string; openingBalances: { accountId: string; balance: number; number: string | null; name: string | null; }[]; }
export interface CloseYearResponse { closingEntry: { id: string } | null; }
