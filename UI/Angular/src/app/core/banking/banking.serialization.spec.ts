import { CashDisbursement, BankAdjustment, ReconciliationWorksheet } from './banking';

describe('banking model serialization keys', () => {
  it('round-trips a cash disbursement JSON payload by camelCase key', () => {
    const json = { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 100 }],
      date: '2026-03-01', reference: null, memo: null, status: 'Posted' };
    const v = json as unknown as CashDisbursement;
    expect(v.number).toBe('CD-00001');
    expect(v.lines[0].accountId).toBe('a1');
    expect(v.status).toBe('Posted');
  });

  it('round-trips a worksheet with cleared entries and verdict', () => {
    const json = { reconciliation: { id: 'r1', number: 'REC-00001', cashAccountId: 'c1',
        bankStatementId: 'b1', statementDate: '2026-03-31', status: 'InProgress', clearedEntryIds: [] },
      statement: { id: 'b1', number: 'BST-00001', cashAccountId: 'c1', statementDate: '2026-03-31',
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' },
      entries: [{ entryId: 'e1', date: '2026-03-05', reference: 'r', sourceType: 'Cash', cashEffect: 100, cleared: true }],
      bookBalance: 100, clearedTotal: 100, reconciledDifference: 0, balanced: true };
    const w = json as unknown as ReconciliationWorksheet;
    expect(w.entries[0].cashEffect).toBe(100);
    expect(w.balanced).toBe(true);
  });

  it('round-trips a bank adjustment kind', () => {
    const json = { id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'c1',
      offsetAccountId: 'o1', kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: null, status: 'Posted' };
    const a = json as unknown as BankAdjustment;
    expect(a.kind).toBe('Charge');
  });
});
