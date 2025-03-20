export type NodeType = FolderNode | AccountNode;

export function isFolder(node: NodeType): node is FolderNode {
  return node.type === 'folder';
}

export function isAccount(node: NodeType): node is AccountNode {
  return node.type === 'account';
}

export function isDraggable(node: NodeType): boolean {
  return isFolder(node) ? node.isDraggable : true;
}

export function findIndex(nodes: NodeType[], id: string): number {
  for (let i = 0; i < nodes.length; i++) {
    if (nodes[i].id === id) return i;
  }
  return -1;
}

export interface FolderNode {
  type: string;
  id: string;
  acctId: string;
  folders: FolderNode[];
  accounts: AccountNode[];
  isExpanded: boolean;
  isDraggable: boolean;
}

export interface AccountNode {
  type: string;
  id: string;
  acctId: string;
}

export interface DropInfo {
  targetId: string;
  action?: string;
}
