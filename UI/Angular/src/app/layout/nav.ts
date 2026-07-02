export interface NavLink { label: string; path: string; area?: string; children?: NavLink[]; }
export interface NavSection { label: string; items: NavLink[]; }

export const NAV: NavSection[] = [
  { label: 'Overview', items: [
    { label: 'Dashboard', path: '/dashboard' },
  ] },
  { label: 'General Ledger', items: [
    { label: 'Journal', path: '/journal', area: 'gl' },
    { label: 'Approvals', path: '/journal/approvals', area: 'gl' },
    { label: 'Chart of Accounts', path: '/accounts', area: 'gl' },
    { label: 'Trial Balance', path: '/trial-balance', area: 'gl' },
    { label: 'Financial Statements', path: '/statements', area: 'gl' },
    { label: 'Period Close', path: '/periods', area: 'gl' },
  ] },
  { label: 'Subledgers', items: [
    { label: 'Receivables', path: '/receivables', area: 'ar' },
    { label: 'Payables', path: '/payables', area: 'ap' },
    { label: 'Payroll', path: '/payroll', area: 'payroll' },
    { label: 'Cash & Banking', path: '/cash', area: 'cash', children: [
      { label: 'Bank Reconciliation', path: '/cash/reconciliation', area: 'bankrec' },
    ] },
    { label: 'Fixed Assets', path: '/fixed-assets', area: 'fixedassets' },
  ] },
  { label: 'Assurance', items: [
    { label: 'Audit', path: '/audit', area: 'audit', children: [
      { label: 'Audit Trail', path: '/audit/trail', area: 'audit' },
      { label: 'Verify Integrity', path: '/audit/verify', area: 'audit' },
      { label: 'Subledger Reconciliations', path: '/audit/reconciliations', area: 'audit' },
    ] },
    { label: 'Reports', path: '/reports', area: 'reports', children: [
      { label: 'Budgets', path: '/reports/budgets', area: 'reports' },
    ] },
  ] },
  { label: 'Administration', items: [
    { label: 'Users & Roles', path: '/admin/users', area: 'admin' },
    { label: 'Firm', path: '/admin/firm', area: 'admin' },
    { label: 'Client', path: '/admin/client', area: 'admin' },
    { label: 'Fiscal settings', path: '/admin/fiscal', area: 'admin' },
    { label: 'Posting accounts', path: '/admin/posting-accounts', area: 'admin' },
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

/** Sections/links the user can see: a link shows if it has no area or `canSee(area)` is true.
 * Children are filtered by their own area; sections with no visible items are dropped. */
export function visibleSections(nav: NavSection[], canSee: (area?: string) => boolean): NavSection[] {
  return nav
    .map((section) => ({
      label: section.label,
      items: section.items
        .filter((item) => canSee(item.area))
        .map((item) => (item.children ? { ...item, children: item.children.filter((c) => canSee(c.area)) } : item)),
    }))
    .filter((section) => section.items.length > 0);
}
