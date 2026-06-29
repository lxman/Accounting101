// `exact` forces exact-URL active matching. Default (prefix) is right for parents whose route
// redirects to a child (e.g. Statements → balance-sheet). Set it only where a sibling nav item is a
// sub-path that would otherwise also light the parent: /journal is a prefix of /journal/approvals.
export interface NavItem { label: string; path: string; exact?: boolean; }

export const NAV: NavItem[] = [
  { label: 'Dashboard', path: '/dashboard' },
  { label: 'Journal', path: '/journal', exact: true },
  { label: 'Approvals', path: '/journal/approvals' },
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
