export interface TreeNode {
  id: string;
  children: TreeNode[];
  isExpanded?:boolean;
  isDraggable?: boolean;
}

export interface DropInfo {
  targetId: string;
  action?: string;
}
