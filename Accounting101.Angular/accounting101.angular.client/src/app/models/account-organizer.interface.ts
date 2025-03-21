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

export function findNodeById(nodes: NodeType[], id: string): NodeType | null {
  for (let i = 0; i < nodes.length; i++) {
    if (nodes[i].id === id) return nodes[i];
  }
  return null;
}

export function findFolderById(nodes: NodeType[], folderId: string): FolderNode | null {
  for (let i = 0; i < nodes.length; i++) {
    const node = nodes[i];
    if (isFolder(node) && node.id === folderId) {
      return node;
    }
    else if (isFolder(node)) {
      return findFolderById(node.children, folderId);
    }
  }
  return null; // Return -1 if the folderId is not found
}

export interface FolderNode {
  type: string;
  id: string;
  name: string;
  children: NodeType[];
  isExpanded: boolean;
  isDraggable: boolean;
}

export interface AccountNode {
  type: string;
  id: string;
  name: string;
}

export interface DropInfo {
  targetId: string;
  action?: string;
}
