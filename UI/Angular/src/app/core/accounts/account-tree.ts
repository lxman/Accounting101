import { AccountResponse, AccountType } from './account';

export interface AccountNode { account: AccountResponse; balance: number; children: AccountNode[]; }
export interface TypeSection { type: AccountType; nodes: AccountNode[]; }
export const TYPE_ORDER: AccountType[] = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense'];

const byNumber = (a: AccountNode, b: AccountNode): number =>
  a.account.number < b.account.number ? -1 : a.account.number > b.account.number ? 1 : 0; // ordinal

export function buildTree(
  accounts: readonly AccountResponse[], balancesById: ReadonlyMap<string, number>, showInactive: boolean,
): TypeSection[] {
  const visible = accounts.filter(a => showInactive || a.active);
  const inType = (type: AccountType) => visible.filter(a => a.type === type);
  return TYPE_ORDER.map(type => {
    const here = inType(type);
    const ids = new Set(here.map(a => a.id));
    const node = (a: AccountResponse): AccountNode => {
      const children = here.filter(c => c.parentId === a.id).map(node).sort(byNumber);
      const own = balancesById.get(a.id) ?? 0;
      return { account: a, balance: own + children.reduce((s, c) => s + c.balance, 0), children };
    };
    // Root within a type = no parent, or parent not present in this type section.
    const roots = here.filter(a => a.parentId === null || !ids.has(a.parentId)).map(node).sort(byNumber);
    return { type, nodes: roots };
  });
}

/** True if candidateId is within ancestorId's subtree (walks candidate's parent chain up to ancestor). */
export function isDescendant(accounts: readonly AccountResponse[], ancestorId: string, candidateId: string): boolean {
  const byId = new Map(accounts.map(a => [a.id, a]));
  let cur = byId.get(candidateId)?.parentId ?? null;
  while (cur !== null) {
    if (cur === ancestorId) return true;
    cur = byId.get(cur)?.parentId ?? null;
  }
  return false;
}

/** Valid drop: same type; if onto an account, not self and not the dragged node's own descendant; if onto a
 *  section root (targetId null), the section's type must equal the dragged account's type. */
export function canDrop(
  accounts: readonly AccountResponse[], draggedId: string, targetId: string | null, sectionType: AccountType,
): boolean {
  const byId = new Map(accounts.map(a => [a.id, a]));
  const dragged = byId.get(draggedId);
  if (!dragged || dragged.type !== sectionType) return false;
  if (targetId === null) return true;                         // drop to this section's root
  if (targetId === draggedId) return false;
  const target = byId.get(targetId);
  if (!target || target.type !== dragged.type) return false;
  return !isDescendant(accounts, draggedId, targetId);        // no cycle
}
