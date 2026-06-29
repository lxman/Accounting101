export type NegativeStyle = 'parens' | 'minus' | 'red' | 'trailing';
export type Scale = 'none' | 'thousands' | 'millions' | 'auto';
export type SymbolPlacement = 'firstAndTotal' | 'every' | 'none';

export interface FormatProfile {
  negativeStyle: NegativeStyle;
  decimals: 0 | 2;
  scale: Scale;
  thousandsSep: boolean;
  currencySymbol: SymbolPlacement;
  zeroDisplay: 'zero' | 'dash';
  dateFormat: string;       // Angular DatePipe format, e.g. 'yyyy-MM-dd'
  accountCodeShown: boolean;
}

export const DEFAULT_FORMAT_PROFILE: FormatProfile = {
  negativeStyle: 'parens', decimals: 2, scale: 'none', thousandsSep: true,
  currencySymbol: 'firstAndTotal', zeroDisplay: 'zero', dateFormat: 'yyyy-MM-dd', accountCodeShown: true,
};
