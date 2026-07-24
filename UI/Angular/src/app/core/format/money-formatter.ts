import { FormatProfile } from './format-profile';

export interface MoneyFormatOptions { symbol?: boolean; }

// Temporary mirror of Iso4217Currencies.Symbols
// (Backend/Accounting101.Ledger.Contracts/Iso4217Currencies.cs), which documents itself as the
// intended single symbol source. This hand-maintained copy exists only because no UI slice yet
// threads a server-supplied currency through formatMoney's call sites (every call site today still
// passes the literal 'USD'). Keep the two in sync until that deferred slice seeds this map from
// GET /currencies/catalog instead and this mirror is retired.
//
// Built prototype-less (Object.create(null)) so a lookup of an untrusted/prototype-ish key (e.g.
// 'constructor', 'toString') can never resolve through Object.prototype — it must fall through to
// the `?? currency + ' '` code fallback below, same as any other unknown code.
const SYMBOLS: Record<string, string> = Object.assign(Object.create(null) as Record<string, string>, {
  USD: '$', EUR: '€', GBP: '£', JPY: '¥', CNY: '¥', INR: '₹', KRW: '₩', RUB: '₽',
  BRL: 'R$', ZAR: 'R', TRY: '₺', ILS: '₪', THB: '฿', PHP: '₱', VND: '₫', NGN: '₦',
  UAH: '₴', PLN: 'zł', CZK: 'Kč', AUD: '$', CAD: '$', NZD: '$', HKD: '$', SGD: '$', MXN: '$',
});

export const isNegativeAmount = (amount: number): boolean => amount < 0;

export function formatMoney(
  amount: number, currency: string, profile: FormatProfile, opts: MoneyFormatOptions = {},
): string {
  const scaled = applyScale(amount, profile.scale);

  const abs = Math.abs(scaled);
  const digits = abs.toFixed(profile.decimals); // rounds to profile decimals

  // Decide negativity AFTER rounding: a tiny magnitude that rounds to zero
  // is zero, not negative-zero. Negative zero is meaningless in accounting.
  const roundedIsZero = Number(digits) === 0;
  if (roundedIsZero) return profile.zeroDisplay === 'dash' ? '—' : digits;

  const negative = scaled < 0;
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
