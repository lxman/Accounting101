import { FormatProfile } from './format-profile';

export interface MoneyFormatOptions { symbol?: boolean; }

const SYMBOLS: Record<string, string> = { USD: '$' };

export const isNegativeAmount = (amount: number): boolean => amount < 0;

export function formatMoney(
  amount: number, currency: string, profile: FormatProfile, opts: MoneyFormatOptions = {},
): string {
  const scaled = applyScale(amount, profile.scale);
  if (scaled === 0 && profile.zeroDisplay === 'dash') return '—';

  const negative = scaled < 0;
  const abs = Math.abs(scaled);

  const digits = abs.toFixed(profile.decimals); // rounds
  const [intPart, fracPart] = digits.split('.');
  const grouped = profile.thousandsSep ? intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ',') : intPart;
  let body = fracPart ? `${grouped}.${fracPart}` : grouped;

  const showSymbol =
    profile.currencySymbol === 'every' ||
    (profile.currencySymbol === 'firstAndTotal' && opts.symbol === true);
  if (showSymbol) body = `${SYMBOLS[currency] ?? currency + ' '}${body}`;

  if (!negative) return body;
  switch (profile.negativeStyle) {
    case 'minus': return `-${body}`;
    case 'trailing': return `${body}-`;
    case 'parens':
    case 'red':   // text identical to parens; the caller applies red via isNegativeAmount
    default: return `(${body})`;
  }
}

function applyScale(amount: number, scale: FormatProfile['scale']): number {
  switch (scale) {
    case 'thousands': return amount / 1_000;
    case 'millions': return amount / 1_000_000;
    case 'auto': return Math.abs(amount) >= 1_000_000 ? amount / 1_000_000
               : Math.abs(amount) >= 1_000 ? amount / 1_000 : amount;
    default: return amount;
  }
}
