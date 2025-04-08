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

export interface FolderNode {
  type: string;
  id: string;
  name: string;
  children: NodeType[];
  isExpanded: boolean;
  isDraggable: boolean;
  folderBalance: number;
}

export interface AccountNode {
  type: string;
  id: string;
  name: string;
  balance: number;
}

export interface DropInfo {
  targetId: string;
  action?: string;
}
