export interface NavItem { label: string; path: string; }

export const NAV: NavItem[] = [
  { label: 'Dashboard', path: '/dashboard' },
  { label: 'Journal', path: '/journal' },
  { label: 'Accounts', path: '/accounts' },
  { label: 'Trial Balance', path: '/trial-balance' },
  { label: 'Statements', path: '/statements' },
  { label: 'Periods', path: '/periods' },
  { label: 'Receivables', path: '/receivables' },
  { label: 'Payables', path: '/payables' },
  { label: 'Payroll', path: '/payroll' },
  { label: 'Cash', path: '/cash' },
  { label: 'Bank Rec', path: '/bank-rec' },
  { label: 'Audit', path: '/audit' },
];
