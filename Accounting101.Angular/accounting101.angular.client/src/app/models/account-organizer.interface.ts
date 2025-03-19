export interface TreeNode {
  id: string;
  acctId: string;
  children: TreeNode[];
  isExpanded?: boolean;
  isDraggable?: boolean;
  isFolder?: boolean;
}

export interface DropInfo {
  targetId: string;
  action?: string;
}
