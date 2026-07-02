import { NAV, navLeafPaths, visibleSections, NavSection } from './nav';

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

describe('visibleSections', () => {
  const sectionLabels = (s: NavSection[]) => s.map(x => x.label);

  it('shows only Overview when nothing is permitted', () => {
    const v = visibleSections(NAV, area => !area);   // only area-less links (Dashboard)
    expect(sectionLabels(v)).toEqual(['Overview']);
  });

  it('shows GL + Receivables for an AR-clerk-like scope', () => {
    const allowed = new Set(['gl', 'ar']);
    const v = visibleSections(NAV, area => !area || allowed.has(area));
    expect(sectionLabels(v)).toEqual(['Overview', 'General Ledger', 'Subledgers']);
    const subledgers = v.find(s => s.label === 'Subledgers')!;
    expect(subledgers.items.map(i => i.path)).toEqual(['/receivables']);
  });

  it('shows Administration when admin is permitted', () => {
    const v = visibleSections(NAV, () => true);
    expect(sectionLabels(v)).toContain('Administration');
  });

  it('filters a parent\'s children by their own area', () => {
    const allowed = new Set(['cash']);   // cash but not bankrec
    const v = visibleSections(NAV, area => !area || allowed.has(area));
    const cash = v.find(s => s.label === 'Subledgers')!.items.find(i => i.path === '/cash')!;
    expect(cash.children ?? []).toEqual([]);
  });
});
