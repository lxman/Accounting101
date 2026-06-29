import { buildTree, isDescendant, canDrop } from './account-tree';
import { AccountResponse, AccountType } from './account';

const acc = (id: string, number: string, type: AccountType, parentId: string | null = null, active = true): AccountResponse =>
  ({ id, number, name: 'n' + number, type, parentId, postable: true, requiredDimension: null,
     cashFlowActivity: null, isRetainedEarnings: false, active, normalSide: 'Debit', isTemporary: false });

describe('buildTree', () => {
  it('groups by type in chart order, nests by parentId, sorts children ordinally, rolls up balances', () => {
    const accounts = [acc('p', '1000', 'Asset'), acc('c2', '1200', 'Asset', 'p'), acc('c1', '1100', 'Asset', 'p'), acc('r', '4000', 'Revenue')];
    const bal = new Map([['p', 0], ['c1', 30], ['c2', 70], ['r', -100]]);
    const sections = buildTree(accounts, bal, false);
    expect(sections.map(s => s.type)).toEqual(['Asset', 'Liability', 'Equity', 'Revenue', 'Expense']);
    const asset = sections.find(s => s.type === 'Asset')!;
    expect(asset.nodes.length).toBe(1);                         // single root 'p'
    expect(asset.nodes[0].children.map(n => n.account.id)).toEqual(['c1', 'c2']); // 1100 before 1200 (ordinal)
    expect(asset.nodes[0].balance).toBe(100);                   // 0 + 30 + 70 rolled up
  });
  it('hides inactive unless showInactive', () => {
    const accounts = [acc('a', '1000', 'Asset'), acc('b', '1100', 'Asset', null, false)];
    expect(buildTree(accounts, new Map(), false).find(s => s.type === 'Asset')!.nodes.length).toBe(1);
    expect(buildTree(accounts, new Map(), true).find(s => s.type === 'Asset')!.nodes.length).toBe(2);
  });
});

describe('isDescendant / canDrop', () => {
  const accounts = [acc('p', '1000', 'Asset'), acc('c', '1100', 'Asset', 'p'), acc('g', '1110', 'Asset', 'c'), acc('rev', '4000', 'Revenue')];
  it('isDescendant walks the parent chain', () => {
    expect(isDescendant(accounts, 'p', 'g')).toBe(true);
    expect(isDescendant(accounts, 'c', 'p')).toBe(false);
  });
  it('canDrop: same type, not self, not own descendant', () => {
    expect(canDrop(accounts, 'c', 'rev', 'Revenue')).toBe(false);  // cross-type
    expect(canDrop(accounts, 'p', 'c', 'Asset')).toBe(false);      // c is p's descendant → cycle
    expect(canDrop(accounts, 'g', 'p', 'Asset')).toBe(true);       // reparent g under p (valid)
    expect(canDrop(accounts, 'g', null, 'Asset')).toBe(true);      // drop to Asset root
    expect(canDrop(accounts, 'g', null, 'Revenue')).toBe(false);   // wrong section root
    expect(canDrop(accounts, 'g', 'g', 'Asset')).toBe(false);      // onto self
  });
});
