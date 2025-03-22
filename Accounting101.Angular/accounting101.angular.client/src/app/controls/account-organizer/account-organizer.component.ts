import {
  ChangeDetectionStrategy,
  Component,
  Inject,
  OnChanges,
  SimpleChanges,
  ViewEncapsulation,
  input,
  inject,
  Input,
  ViewChild,
  model, ChangeDetectorRef
} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatInputModule} from '@angular/material/input';
import {MatButtonModule} from '@angular/material/button';
import {MatDialog} from '@angular/material/dialog';
import {CommonModule} from '@angular/common';
import {DOCUMENT, NgForOf, NgTemplateOutlet} from '@angular/common';
import {
  FolderNode,
  DropInfo,
  AccountNode,
  isFolder,
  isAccount,
  isDraggable,
  NodeType
} from '../../models/account-organizer.interface';
import {debounce} from '@agentepsilon/decko'
import {CdkDrag, CdkDropList} from '@angular/cdk/drag-drop';
import {AccountModel} from '../../models/account.model';
import {AccountGroupModel} from '../../models/account-group.model';
import {v7 as uuidv7} from 'uuid';
import {MatMenu, MatMenuContent, MatMenuItem, MatMenuTrigger} from '@angular/material/menu';
import {GetFolderName} from '../../dialogs/get-folder-name/get-folder-name.component';
import {DeleteFolderConfirm} from '../../dialogs/delete-folder-confirm/delete-folder-confirm.component';

@Component({
  selector: 'app-account-organizer',
  imports: [
    CdkDropList,
    CdkDrag,
    NgTemplateOutlet,
    NgForOf,
    CommonModule,
    MatMenu,
    MatMenuTrigger,
    MatMenuContent,
    MatMenuItem,
    MatFormFieldModule,
    MatInputModule,
    FormsModule,
    MatButtonModule
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './account-organizer.component.html',
  styleUrl: './account-organizer.component.scss',
  encapsulation: ViewEncapsulation.None
})

export class AccountOrganizerComponent implements OnChanges{
  @Input() groupName: string = '';
  readonly layoutGroup = input.required<AccountGroupModel>();
  readonly accounts = input.required<AccountModel[]>();
  readonly name = model('');
  readonly folderNameDialog = inject(MatDialog);
  readonly deleteFolderConfirmDialog = inject(MatDialog);
  protected readonly isFolder = isFolder;
  protected readonly isAccount = isAccount;
  protected readonly isDraggable = isDraggable;

  nodes: NodeType[] = [];

  dropTargetIds: string[] = [];
  nodeLookup: { [key: string]: NodeType } = {};
  dropActionTodo: DropInfo | null = null;

  @ViewChild(MatMenuTrigger)
  contextMenu!: MatMenuTrigger;

  contextMenuPosition = { x: '0px', y: '0px' };

  constructor(
    @Inject(DOCUMENT) private document: Document,
    private changeDetectorRef: ChangeDetectorRef) {
  }

  onContextMenu(event: MouseEvent, item: FolderNode) {
    event.preventDefault();
    event.stopPropagation();
    this.contextMenuPosition.x = event.clientX + 'px';
    this.contextMenuPosition.y = event.clientY + 'px';
    this.contextMenu.menuData = {
      x: event.clientX,
      y: event.clientY,
      item: item
    };
    this.contextMenu.menu?.focusFirstItem('mouse');
    this.contextMenu.openMenu();
  }

  onContextMenuNew(item: FolderNode) {
    const dialogRef = this.folderNameDialog.open(GetFolderName, {
      data: {name: this.name()}
    });
    dialogRef.afterClosed().subscribe(result => {
      if (result) {
        const newFolder: FolderNode = {
          type: 'folder',
          name: result,
          id: uuidv7(),
          isDraggable: true,
          children: new Array<NodeType>(),
          isExpanded: false
        }
        item.children.push(newFolder);
        item.isExpanded = true;
        this.changeDetectorRef.detectChanges();
        console.log("Folder " + newFolder.name + " created");
      }
    })
  }

  onContextMenuDelete(item: FolderNode) {
    const dialogRef = this.deleteFolderConfirmDialog.open(DeleteFolderConfirm, {
      data: {confirm: false},
      autoFocus: false
    })
    dialogRef.afterClosed().subscribe(result => {
      if (result) {
        alert("Delete Folder");
      }
    })
  }

  ngOnChanges(changes: SimpleChanges) {
    if (!changes['layoutGroup'].firstChange && !changes['accounts'].firstChange) {
      this.buildTree();
      this.prepareDragDrop(this.nodes.filter(n => n.type == 'folder') as FolderNode[]);
    }
  }

  prepareDragDrop(nodes: FolderNode[]) {
    nodes.forEach(node => {
      node.name = node.name.replace(/ /g, "_")
      this.dropTargetIds.push(node.id);
      this.nodeLookup[node.id] = node;
      this.prepareDragDrop(node.children.filter(n => n.type == 'folder') as FolderNode[]);
    });
  }

  private buildTree() {
    const layoutGroup = this.layoutGroup();
    const accounts = this.accounts();
    const rootNode: FolderNode = {
      type: 'folder',
      name: this.groupName,
      id: layoutGroup.id,
      isDraggable: false,
      children: new Array<NodeType>(),
      isExpanded: false
    }
    this.nodes.push(rootNode);
    if ((layoutGroup.groups) || (accounts)) {
      this.addFolders(rootNode, layoutGroup, accounts);
    }
  }

  private addFolders(parent: FolderNode, group: AccountGroupModel, accounts: AccountModel[]) {
    group.groups?.forEach(group => {
      const node: FolderNode = {
        type: 'folder',
        name: group.name,
        id: group.id,
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
          this.addFolders(node, group, accounts);
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
        name: accountName ?? '',
        id: acctId,
      }
      parent.children.push(node);
    });
  }

  @debounce(50)
  dragMoved(event: { pointerPosition: { x: number; y: number; }; }) {
    const potentialDestinationContainer = this.document.elementFromPoint(event.pointerPosition.x, event.pointerPosition.y);
    const isFolder = potentialDestinationContainer?.classList.contains("folder-node");
    if (!potentialDestinationContainer) {
      this.clearDragInfo();
      return;
    }
    this.dropActionTodo = {
      targetId: potentialDestinationContainer.getAttribute("data-id") || ''
    };
    const targetRect = potentialDestinationContainer.getBoundingClientRect();
    const oneThird = targetRect.height / 3;

    if (event.pointerPosition.y - targetRect.top < oneThird) {
      // before
      this.dropActionTodo["action"] = "before";
    } else if (event.pointerPosition.y - targetRect.top > 2 * oneThird) {
      // after
      this.dropActionTodo["action"] = "after";
    } else {
      // inside
      if (!isFolder) {
        this.clearDragInfo();
        return;
      }
      this.dropActionTodo["action"] = "inside";
    }
    this.showDragInfo(event);
  }

  drop(event: { item: { data: any; }; previousContainer: { id: any; }; container: { data: any; }; }) {
    if (!this.dropActionTodo) return;

    const srcFolder = event.container.data as FolderNode;
    const srcIndex = srcFolder.children.findIndex(c => c.id == event.item.data);
    const parentItem = event.previousContainer;
    const target = this.getDropNode(this.dropActionTodo.targetId, this.nodes);

    if (!target || target.id == 'main' || parentItem.id == 'main') {
      this.clearDragInfo();
      return;
    }

    // Remove from the old
    const movedNode = srcFolder.children.splice(srcIndex, 1)[0];

    // Dropping on a folder (inside)
    if (target.type == 'folder' && this.dropActionTodo.action == 'inside' && "children" in target) {
      // Insert into the new
      target.children.splice(srcIndex, 0, movedNode);
    }

    // Dropping before or after a folder or account
    if (this.dropActionTodo.action == 'before' || this.dropActionTodo.action == 'after') {
      const targetFolder = this.getContainingFolder(target.id);
      if (!targetFolder) {
        console.log("target folder not found");
        return;
      }
      const destFolder = targetFolder[0];
      const index = targetFolder[1] + (this.dropActionTodo.action == "before" ? 0 : 1);
      destFolder.children.splice(index, 0, movedNode);
    }

    this.clearDragInfo(true)
  }

  getDropNode(id: string, nodesToSearch: NodeType[]): NodeType | null {
    for (let node of nodesToSearch) {
      if (node.id == id) {
        return node;
      }
      if ("children" in node) {
        let ret = this.getDropNode(id, node.children);
        if (ret) return ret;
      }
    }
    return null;
  }

  getContainingFolder(id: string): [FolderNode, number] | null {
    for (let node of this.nodes) {
      if (isFolder(node) && node.children.findIndex(c => c.id == id) > -1) {
        return [node as FolderNode, node.children.findIndex(c => c.id == id)];
      }
    }
    return null;
  }

  showDragInfo(event: { pointerPosition: { x: number; y: number; }; }) {
    this.clearDragInfo();
    if (this.dropActionTodo && this.dropActionTodo.targetId.length > 0) {
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
