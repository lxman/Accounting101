import {Component, Inject, OnChanges, SimpleChanges, ViewEncapsulation, input, Input} from '@angular/core';
import {CommonModule} from '@angular/common';
import {DOCUMENT, NgForOf, NgTemplateOutlet} from '@angular/common';
import {
  FolderNode,
  DropInfo,
  AccountNode,
  isFolder,
  isAccount,
  isDraggable,
  findNodeById,
  findFolderByFolderId, NodeType
} from '../../models/account-organizer.interface';
import {debounce} from '@agentepsilon/decko'
import {CdkDrag, CdkDropList} from '@angular/cdk/drag-drop';
import {AccountModel} from '../../models/account.model';
import {AccountGroupModel} from '../../models/account-group.model';
import {v7 as uuidv7} from 'uuid';

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

  nodes: NodeType[] = [];

  dropTargetIds: string[] = [];
  nodeLookup: { [key: string]: NodeType } = {};
  dropActionTodo: DropInfo | null = null;

  constructor(@Inject(DOCUMENT) private document: Document) {
  }

  contextMenu(event: MouseEvent) {
    console.log("right click");
    event.preventDefault();
    event.stopPropagation();
    const container = this.getClickedElementFolder(event.clientX, event.clientY);
    const targetId = container?.getAttribute("data-id");
    console.log(container);
    console.log(targetId);
    if (!container || !targetId) {
      return;
    }
    const parentNode = findFolderByFolderId(this.nodes, targetId);
    if (!parentNode) {
      console.log("parentNode not found");
      return;
    }
    console.log("parentNode: " + JSON.stringify(parentNode));
    const newFolder: FolderNode = {
      type: 'folder',
      id: 'Test Folder',
      acctId: '',
      folderId: uuidv7(),
      isDraggable: true,
      children: new Array<NodeType>(),
      isExpanded: false
    }
    parentNode.children.push(newFolder);
    console.log(this.nodes);
  }

  ngOnChanges(changes: SimpleChanges) {
    if (!changes['layoutGroup'].firstChange && !changes['accounts'].firstChange) {
      this.buildTree();
      this.prepareDragDrop(this.nodes.filter(n => n.type == 'folder') as FolderNode[]);
    }
  }

  private buildTree() {
    const layoutGroup = this.layoutGroup();
    const accounts = this.accounts();
    const rootNode: FolderNode = {
      type: 'folder',
      id: this.groupName,
      acctId: '',
      folderId: layoutGroup.id,
      isDraggable: false,
      children: new Array<NodeType>(),
      isExpanded: false
    }
    this.nodes.push(rootNode);
    if ((layoutGroup.groups) || (accounts)) {
      this.buildNodes(rootNode, layoutGroup, accounts);
      console.log(this.nodes);
    }
  }

  private buildNodes(parent: FolderNode, group: AccountGroupModel, accounts: AccountModel[]) {
    group.groups?.forEach(group => {
      const node: FolderNode = {
        type: 'folder',
        id: group.name,
        acctId: '',
        folderId: group.id,
        isDraggable: true,
        children: new Array<NodeType>(),
        isExpanded: false
      }
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

  private addAccounts(parent: FolderNode, acctIds: string[], accounts: AccountModel[]) {
    acctIds.forEach(acctId => {
      const accountName = accounts.find(a => a.id == acctId)?.info?.name;
      const node: AccountNode = {
        type: 'account',
        id: accountName ?? '',
        acctId: acctId
      }
      parent.children.push(node);
    });
  }

  prepareDragDrop(nodes: NodeType[]) {
    nodes.forEach(node => {
      if ("children" in node) {
        node.id = node.id.replace(/ /g, "_")
        this.dropTargetIds.push(node.id);
        this.nodeLookup[node.id] = node;
        this.prepareDragDrop(node.children);
      }
      //this.prepareDragDrop(node.children);
    });
  }

  @debounce(50)
  dragMoved(event: { pointerPosition: { x: number; y: number; }; }) {
    const container = this.getClickedElementRoot(event.pointerPosition.x, event.pointerPosition.y);
    if (!container || container.getAttribute("draggable") === "false") {
      this.clearDragInfo();
      return;
    }
    this.dropActionTodo = {
      targetId: container.getAttribute("data-id") || ''
    };
    console.log(this.dropActionTodo);
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

    const draggedItem = event.item.data;
    const parentItem = event.previousContainer;
    const targetListId = this.getDropDestinationId(this.dropActionTodo.targetId, this.nodes);

    console.log("Requested targetId: " + this.dropActionTodo.targetId);
    console.log("draggedItem: " + draggedItem);
    console.log("parentItem: " + parentItem);
    console.log("targetListId: " + targetListId);

    if (!targetListId || targetListId == 'main' || parentItem.id == 'main') {
      this.clearDragInfo();
      return;
    }

    const oldItemContainer = this.nodeLookup[parentItem.id] as FolderNode;
    const newItemContainer = this.nodeLookup[targetListId] as FolderNode;
    let node = findNodeById(oldItemContainer.children, draggedItem.id);

    // Remove the old
    switch (draggedItem.type) {
      case 'folder':
        break;
      case 'account':
        break;
    }

    // Insert the new
    switch (this.dropActionTodo.action) {
      case 'before':
      case 'after':
        break;
      case 'inside':
        break;
    }
    // switch (draggedItem.type) {
    //   case 'folder':
    //     oldItemContainer.folders.splice(node, 1);
    //     break;
    //   case 'account':
    //     oldItemContainer.accounts.splice(node, 1);
    //     break;
    // }
    //
    // switch (this.dropActionTodo.action) {
    //   case 'before':
    //   case 'after':
    //     const targetNode = findNodeById(newContainer.accounts, this.dropActionTodo?.targetId);
    //     if (this.dropActionTodo.action == 'before') {
    //       newContainer.accounts.splice(targetNode, 0, draggedItem);
    //     }
    //     else {
    //       newContainer.accounts.splice(targetNode + 1, 0, draggedItem);
    //     }
    //     break;
    // }

    this.clearDragInfo(true)
  }

  getDropDestinationId(folderId: string, nodesToSearch: NodeType[]): string | null {
    for (let node of nodesToSearch) {
      if ("children" in node) {
        if (node.folderId == folderId) return node.id;
        let ret = this.getDropDestinationId(folderId, node.children);
        if (ret) return ret;
      }
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
  }

  private getClickedElementRoot(x: number, y: number) {
    let e = this.document.elementFromPoint(x,y);
    if (!e) {
      return null;
    }
    let container = e.classList.contains("root-folder") ? e : e.closest(".root-folder");
    if (!container) {
      return null;
    }
    return container;
  }

  private getClickedElementFolder(x: number, y: number) {
    let e = this.document.elementFromPoint(x,y);
    if (!e) {
      return null;
    }
    let container = e.classList.contains("folder-node") ? e : e.closest(".folder-node");
    if (!container) {
      return null;
    }
    return container;
  }

  protected readonly isFolder = isFolder;
  protected readonly isAccount = isAccount;
  protected readonly isDraggable = isDraggable;
}
