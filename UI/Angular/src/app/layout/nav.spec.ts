import { NAV, navLeafPaths, NavSection } from './nav';

describe('nav', () => {
  it('has the five north-star sections in order', () => {
    expect(NAV.map((s: NavSection) => s.label)).toEqual([
      'Overview', 'General Ledger', 'Subledgers', 'Assurance', 'Administration',
    ]);
  });

  it('navLeafPaths returns every path (parents + children), no duplicates', () => {
    const paths = navLeafPaths();
    expect(new Set(paths).size).toBe(paths.length); // no dupes
    expect(paths).toEqual(expect.arrayContaining([
      '/dashboard',
      '/journal', '/journal/approvals', '/accounts', '/trial-balance', '/statements', '/periods',
      '/receivables', '/payables', '/payroll',
      '/cash', '/cash/reconciliation', '/fixed-assets',
      '/audit', '/audit/trail', '/audit/verify', '/audit/reconciliations',
      '/reports', '/reports/budgets',
      '/admin/users', '/admin/firm', '/admin/client', '/admin/fiscal', '/admin/posting-accounts',
    ]));
    expect(paths.length).toBe(24);
  });

  it('nests Bank Reconciliation under Cash & Banking', () => {
    const subledgers = NAV.find((s) => s.label === 'Subledgers')!;
    const cash = subledgers.items.find((i) => i.path === '/cash')!;
    expect(cash.label).toBe('Cash & Banking');
    expect(cash.children?.map((c) => c.path)).toEqual(['/cash/reconciliation']);
  });
});
