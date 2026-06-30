import { billTotal, autoAllocate, AllocRow } from './payables';

describe('billTotal', () => {
  it('sums line amounts', () => {
    expect(billTotal([{ amount: 100 }, { amount: 49.5 }, { amount: 0 }])).toBe(149.5);
  });
  it('is 0 for no lines', () => {
    expect(billTotal([])).toBe(0);
  });
});

describe('autoAllocate', () => {
  const rows = (): AllocRow[] => [
    { billId: 'b1', number: 'B-1', billDate: '2026-01-01', openBalance: 100, allocation: 0 },
    { billId: 'b2', number: 'B-2', billDate: '2026-02-01', openBalance: 50, allocation: 0 },
  ];
  it('fills oldest-first, capped at each open balance', () => {
    const out = autoAllocate(120, rows());
    expect(out.map(r => r.allocation)).toEqual([100, 20]);
  });
  it('leaves a remainder unallocated when amount exceeds total open', () => {
    const out = autoAllocate(200, rows());
    expect(out.map(r => r.allocation)).toEqual([100, 50]); // 50 remainder → vendor credit
  });
  it('allocates nothing for a zero amount', () => {
    expect(autoAllocate(0, rows()).map(r => r.allocation)).toEqual([0, 0]);
  });
});
