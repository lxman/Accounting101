import { formatMoney, isNegativeAmount } from './money-formatter';
import { DEFAULT_FORMAT_PROFILE as D, FormatProfile } from './format-profile';
const P = (o: Partial<FormatProfile>): FormatProfile => ({ ...D, ...o });

describe('formatMoney', () => {
  it('defaults: thousands sep, 2dp, parens negatives, no symbol unless asked', () => {
    expect(formatMoney(1234, 'USD', D)).toBe('1,234.00');
    expect(formatMoney(-29, 'USD', D)).toBe('(29.00)');
    expect(formatMoney(-450.5, 'USD', D)).toBe('(450.50)');
  });
  it('negative styles', () => {
    expect(formatMoney(-29, 'USD', P({ negativeStyle: 'minus' }))).toBe('-29.00');
    expect(formatMoney(-29, 'USD', P({ negativeStyle: 'trailing' }))).toBe('29.00-');
    expect(formatMoney(-29, 'USD', P({ negativeStyle: 'red' }))).toBe('(29.00)'); // text = parens; caller colors
    expect(isNegativeAmount(-29)).toBe(true);
    expect(isNegativeAmount(0)).toBe(false);
  });
  it('decimals 0', () => {
    expect(formatMoney(1234.56, 'USD', P({ decimals: 0 }))).toBe('1,235');
  });
  it('scale thousands/millions (divide; header carries the unit)', () => {
    expect(formatMoney(1_234_000, 'USD', P({ scale: 'thousands' }))).toBe('1,234.00');
    expect(formatMoney(2_500_000, 'USD', P({ scale: 'millions' }))).toBe('2.50');
  });
  it('thousands separator off', () => {
    expect(formatMoney(1234.5, 'USD', P({ thousandsSep: false }))).toBe('1234.50');
  });
  it('currency symbol placement', () => {
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'every' }))).toBe('$1,234.00');
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'firstAndTotal' }), { symbol: true })).toBe('$1,234.00');
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'firstAndTotal' }), { symbol: false })).toBe('1,234.00');
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'none' }), { symbol: true })).toBe('1,234.00');
  });
  it('negative with symbol keeps symbol inside the sign treatment', () => {
    expect(formatMoney(-29, 'USD', P({ currencySymbol: 'every' }))).toBe('($29.00)');
  });
  it('zero display', () => {
    expect(formatMoney(0, 'USD', D)).toBe('0.00');
    expect(formatMoney(0, 'USD', P({ zeroDisplay: 'dash' }))).toBe('—');
  });
});
