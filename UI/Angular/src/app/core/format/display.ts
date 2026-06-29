import { formatMoney } from './money-formatter';
import { formatProfileDate } from './date-formatter';
import { DEFAULT_FORMAT_PROFILE } from './format-profile';

/** Money for display: USD, no symbol (symbol shows on totals only, per the profile), parens negatives. */
export const money = (n: number): string => formatMoney(n, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false });

/** A date string rendered through the active Format Profile. */
export const displayDate = (d: string): string => formatProfileDate(d, DEFAULT_FORMAT_PROFILE);
