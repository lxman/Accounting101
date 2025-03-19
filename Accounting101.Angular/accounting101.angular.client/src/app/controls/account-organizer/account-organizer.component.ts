import {Component, Inject, OnChanges, SimpleChanges, ViewEncapsulation, input, Input} from '@angular/core';
import {CommonModule} from '@angular/common';
import {DOCUMENT, NgForOf, NgTemplateOutlet} from '@angular/common';
import {DropInfo, TreeNode} from '../../models/account-organizer.interface';
import {debounce} from '@agentepsilon/decko'
import {CdkDrag, CdkDropList} from '@angular/cdk/drag-drop';
import {AccountModel} from '../../models/account.model';
import {AccountGroupModel} from '../../models/account-group.model';
import {AccountListComponent} from '../../components/account-list/account-list.component';

@Component({
  selector: 'app-account-organizer',
  imports: [
    CdkDropList,
    CdkDrag,
    NgTemplateOutlet,
    NgForOf,
    CommonModule
  ],
  templateUrl: './account-organizer.component.html',
  styleUrl: './account-organizer.component.scss',
  encapsulation: ViewEncapsulation.None
})

export class AccountOrganizerComponent implements OnChanges{
  @Input() groupName: string = '';
  readonly layoutGroup = input.required<AccountGroupModel>();
  readonly accounts = input.required<AccountModel[]>();

  nodes: TreeNode[] = [];

  // ids for connected drop lists
  dropTargetIds: string[] = [];
  nodeLookup: { [key: string]: TreeNode } = {};
  dropActionTodo: DropInfo | null = null;

  constructor(@Inject(DOCUMENT) private document: Document) {
  }

  ngOnChanges(changes: SimpleChanges) {
    if (!changes['layoutGroup'].firstChange && !changes['accounts'].firstChange) {
      this.buildTree();
      this.prepareDragDrop(this.nodes);
    }
  }

  private buildTree() {
    const layoutGroup = this.layoutGroup();
    const accounts = this.accounts();
    const rootNode = {
      id: this.groupName,
      acctId: layoutGroup.id,
      children: new Array<TreeNode>(),
      isDraggable: false
    }
    this.nodes.push(rootNode);
    if ((layoutGroup.groups) || (accounts)) {
      this.buildNodes(rootNode, layoutGroup, accounts);
    }
  }

  private buildNodes(parent: TreeNode, group: AccountGroupModel, accounts: AccountModel[]) {
    group.groups?.forEach(group => {
      const node = {
        id: group.name,
        acctId: group.id,
        children: new Array<TreeNode>(),
        isDraggable: true
      };
      parent.children.push(node);
      if (group.accounts && group.accounts.length > 0) {
        this.addAccounts(node, group.accounts, accounts);
      }
      if (group.groups && group.groups.length > 0) {
        group.groups?.forEach(group => {
          this.buildNodes(node, group, accounts);
        })
      }
    });
    if (group.accounts && group.accounts.length > 0) {
      this.addAccounts(parent, group.accounts, accounts);
    }
  }

  private addAccounts(parent: TreeNode, acctIds: string[], accounts: AccountModel[]) {
    acctIds.forEach(acctId => {
      const accountName = accounts.find(a => a.id == acctId)?.info?.name;
      const node = {
        id: accountName ?? '',
        acctId: acctId,
        children: new Array<TreeNode>(),
        isDraggable: true
      };
      parent.children.push(node);
    });
  }

  prepareDragDrop(nodes: TreeNode[]) {
    nodes.forEach(node => {
      this.dropTargetIds.push(node.id);
      this.nodeLookup[node.id] = node;
      this.prepareDragDrop(node.children);
    });
  }

  @debounce(50)
  dragMoved(event: { pointerPosition: { x: number; y: number; }; }) {
    let e = this.document.elementFromPoint(event.pointerPosition.x,event.pointerPosition.y);

    if (!e) {
      this.clearDragInfo();
      return;
    }
    let container = e.classList.contains("node-item") ? e : e.closest(".node-item");
    if (!container || container.getAttribute("draggable") === "false") {
      this.clearDragInfo();
      return;
    }
    this.dropActionTodo = {
      targetId: container.getAttribute("data-id") || ''
    };
    const targetRect = container.getBoundingClientRect();
    const oneThird = targetRect.height / 3;

    if (event.pointerPosition.y - targetRect.top < oneThird) {
      // before
      this.dropActionTodo["action"] = "before";
    } else if (event.pointerPosition.y - targetRect.top > 2 * oneThird) {
      // after
      this.dropActionTodo["action"] = "after";
    } else {
      // inside
      this.dropActionTodo["action"] = "inside";
    }
    this.showDragInfo();
  }

  drop(event: { item: { data: any; }; previousContainer: { id: any; }; }) {
    if (!this.dropActionTodo) return;

    const draggedItemId = event.item.data;
    const parentItemId = event.previousContainer.id;
    const targetListId = this.getParentNodeId(this.dropActionTodo.targetId, this.nodes, 'main');

    if (parentItemId != targetListId) {
      this.clearDragInfo();
      return;
    }

    console.log(
      '\nmoving\n[' + draggedItemId + '] from list [' + parentItemId + ']',
      '\n[' + this.dropActionTodo.action + ']\n[' + this.dropActionTodo.targetId + '] from list [' + targetListId + ']');

    const draggedItem = this.nodeLookup[draggedItemId];

    const oldItemContainer = parentItemId != 'main' ? this.nodeLookup[parentItemId].children : this.nodes;
    const newContainer = targetListId && targetListId != 'main' ? this.nodeLookup[targetListId].children : this.nodes;

    let i = oldItemContainer.findIndex(c => c.id === draggedItemId);
    oldItemContainer.splice(i, 1);

    switch (this.dropActionTodo.action) {
      case 'before':
      case 'after':
        const targetIndex = newContainer.findIndex(c => c.id === this.dropActionTodo?.targetId);
        if (this.dropActionTodo.action == 'before') {
          newContainer.splice(targetIndex, 0, draggedItem);
        } else {
          newContainer.splice(targetIndex + 1, 0, draggedItem);
        }
        break;

      case 'inside':
        this.nodeLookup[this.dropActionTodo.targetId].children.push(draggedItem)
        this.nodeLookup[this.dropActionTodo.targetId].isExpanded = true;
        break;
    }

    this.clearDragInfo(true)
  }

  getParentNodeId(id: string, nodesToSearch: TreeNode[], parentId: string): string | null {
    for (let node of nodesToSearch) {
      if (node.id == id) return parentId;
      let ret = this.getParentNodeId(id, node.children, node.id);
      if (ret) return ret;
    }
    return null;
  }

  showDragInfo() {
    this.clearDragInfo();
    if (this.dropActionTodo) {
      this.document.getElementById("node-" + this.dropActionTodo.targetId)?.classList.add("drop-" + this.dropActionTodo.action);
    }
  }

  clearDragInfo(dropped = false) {
    if (dropped) {
      this.dropActionTodo = null;
    }
    this.document
      .querySelectorAll(".drop-before")
      .forEach(element => element.classList.remove("drop-before"));
    this.document
      .querySelectorAll(".drop-after")
      .forEach(element => element.classList.remove("drop-after"));
    this.document
      .querySelectorAll(".drop-inside")
      .forEach(element => element.classList.remove("drop-inside"));
  }
}
