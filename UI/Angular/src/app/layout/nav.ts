export interface NavLink { label: string; path: string; children?: NavLink[]; }
export interface NavSection { label: string; items: NavLink[]; }

export const NAV: NavSection[] = [
  { label: 'Overview', items: [
    { label: 'Dashboard', path: '/dashboard' },
  ] },
  { label: 'General Ledger', items: [
    { label: 'Journal', path: '/journal' },
    { label: 'Approvals', path: '/journal/approvals' },
    { label: 'Chart of Accounts', path: '/accounts' },
    { label: 'Trial Balance', path: '/trial-balance' },
    { label: 'Financial Statements', path: '/statements' },
    { label: 'Period Close', path: '/periods' },
  ] },
  { label: 'Subledgers', items: [
    { label: 'Receivables', path: '/receivables' },
    { label: 'Payables', path: '/payables' },
    { label: 'Payroll', path: '/payroll' },
    { label: 'Cash & Banking', path: '/cash', children: [
      { label: 'Bank Reconciliation', path: '/cash/reconciliation' },
    ] },
    { label: 'Fixed Assets', path: '/fixed-assets' },
  ] },
  { label: 'Assurance', items: [
    { label: 'Audit', path: '/audit', children: [
      { label: 'Audit Trail', path: '/audit/trail' },
      { label: 'Verify Integrity', path: '/audit/verify' },
      { label: 'Subledger Reconciliations', path: '/audit/reconciliations' },
    ] },
    { label: 'Reports', path: '/reports', children: [
      { label: 'Budgets', path: '/reports/budgets' },
    ] },
  ] },
  { label: 'Administration', items: [
    { label: 'Users & Roles', path: '/admin/users' },
    { label: 'Firm', path: '/admin/firm' },
    { label: 'Client', path: '/admin/client' },
    { label: 'Fiscal settings', path: '/admin/fiscal' },
    { label: 'Posting accounts', path: '/admin/posting-accounts' },
  ] },
];

export function navLeafPaths(): string[] {
  const out: string[] = [];
  const walk = (links: NavLink[]): void => {
    for (const l of links) {
      out.push(l.path);
      if (l.children) walk(l.children);
    }
  };
  for (const section of NAV) walk(section.items);
  return out;
}
