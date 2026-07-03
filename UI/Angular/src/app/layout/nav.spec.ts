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
      '/admin/users', '/admin/access/sets', '/admin/firm', '/admin/client', '/admin/fiscal', '/admin/posting-accounts',
    ]));
    expect(paths.length).toBe(25);
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
    // Area-less links only (Dashboard); a deployment-admin link is not permitted here.
    const v = visibleSections(NAV, (link) => !link.area && !link.deploymentAdmin);
    expect(sectionLabels(v)).toEqual(['Overview']);
  });

  it('shows GL + Receivables for an AR-clerk-like scope', () => {
    const allowed = new Set(['gl', 'ar']);
    const v = visibleSections(NAV, (link) => (!link.area || allowed.has(link.area)) && !link.deploymentAdmin);
    expect(sectionLabels(v)).toEqual(['Overview', 'General Ledger', 'Subledgers']);
    const subledgers = v.find(s => s.label === 'Subledgers')!;
    expect(subledgers.items.map(i => i.path)).toEqual(['/receivables']);
  });

  it('shows Administration only when admin is permitted', () => {
    const withAdmin = visibleSections(NAV, (link) => !link.area || link.area === 'admin');
    expect(sectionLabels(withAdmin)).toContain('Administration');

    const withoutAdmin = visibleSections(NAV, (link) => (!link.area || link.area === 'gl') && !link.deploymentAdmin);
    expect(sectionLabels(withoutAdmin)).not.toContain('Administration');
  });

  it('drops the children array when all sub-items are filtered out (no dead caret)', () => {
    // Permit 'cash' (Cash & Banking) but not 'bankrec' (its only child, Bank Reconciliation).
    const v = visibleSections(NAV, (link) => !link.area || link.area === 'cash');
    const cash = v.find((s) => s.label === 'Subledgers')!.items.find((i) => i.path === '/cash')!;
    expect(cash.children).toBeUndefined();
  });

  it('filters a parent\'s children by their own area', () => {
    const allowed = new Set(['cash']);   // cash but not bankrec
    const v = visibleSections(NAV, (link) => !link.area || allowed.has(link.area));
    const cash = v.find(s => s.label === 'Subledgers')!.items.find(i => i.path === '/cash')!;
    expect(cash.children).toBeUndefined();
  });

  it('hides deployment-admin links from non-deployment-admins', () => {
    const seen = visibleSections(NAV, (l) => (!l.area || l.area === 'admin') && !l.deploymentAdmin);
    const admin = seen.find((s) => s.label === 'Administration');
    expect(admin?.items.some((i) => i.path === '/admin/access/sets')).toBe(false);
  });

  it('shows deployment-admin links when permitted', () => {
    const seen = visibleSections(NAV, (l) => (!l.area || l.area === 'admin') && (!l.deploymentAdmin || true));
    const admin = seen.find((s) => s.label === 'Administration');
    expect(admin?.items.some((i) => i.path === '/admin/access/sets')).toBe(true);
  });

  it('shows Capability Sets to a pure deployment admin (no admin.* capability)', () => {
    // Models Dev Approver: deployment admin (admin=true) but no admin.* capability, so hasArea('admin') is false.
    const isDeploymentAdmin = true;
    const seen = visibleSections(NAV, (l) =>
      (!l.area || false) && (!l.deploymentAdmin || isDeploymentAdmin));
    const admin = seen.find((s) => s.label === 'Administration');
    expect(admin?.items.some((i) => i.path === '/admin/access/sets')).toBe(true);
    // ...but NOT the admin.*-gated items (Users & Roles etc.).
    expect(admin?.items.some((i) => i.path === '/admin/users')).toBe(false);
  });
});
