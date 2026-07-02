import { routes } from './app.routes';
import { navLeafPaths } from './layout/nav';

// Collect every concrete (non-param, non-wildcard) path the route table can match,
// expanding one level of children with their parent prefix.
function matchablePaths(): Set<string> {
  const set = new Set<string>();
  for (const r of routes) {
    if (!r.path || r.path === '**') continue;
    set.add('/' + r.path);
    for (const c of (r.children ?? [])) {
      if (c.path) set.add('/' + r.path + '/' + c.path);
    }
  }
  return set;
}

describe('app.routes', () => {
  it('resolves every nav leaf path to a route (no dead links)', () => {
    const matchable = matchablePaths();
    const missing = navLeafPaths().filter((p) => !matchable.has(p));
    expect(missing).toEqual([]);
  });

  it('registers placeholder routes for unbuilt destinations', () => {
    const matchable = matchablePaths();
    expect(matchable.has('/periods')).toBe(true);
    expect(matchable.has('/cash')).toBe(true);
    expect(matchable.has('/cash/reconciliation')).toBe(true);
    expect(matchable.has('/admin/users')).toBe(true);
    expect(matchable.has('/reports/budgets')).toBe(true);
  });
});
