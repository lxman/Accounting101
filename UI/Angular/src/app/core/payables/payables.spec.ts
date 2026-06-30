import { billTotal } from './payables';

describe('billTotal', () => {
  it('sums line amounts', () => {
    expect(billTotal([{ amount: 100 }, { amount: 49.5 }, { amount: 0 }])).toBe(149.5);
  });
  it('is 0 for no lines', () => {
    expect(billTotal([])).toBe(0);
  });
});
