import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  Inject,
  inject,
  input,
  Input,
  model,
  OnChanges, output,
  SimpleChanges,
  ViewChild,
  ViewEncapsulation
} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatInputModule} from '@angular/material/input';
import {MatButtonModule} from '@angular/material/button';
import {MatDialog} from '@angular/material/dialog';
import {CommonModule, DOCUMENT, NgForOf, NgTemplateOutlet} from '@angular/common';
import {
  AccountNode,
  DropInfo,
  FolderNode,
  isAccount,
  isDraggable,
  isFolder,
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
import {AccountGroupListItemType} from '../../enums/account-group-list-item-type.enum';
import {AccountGroupListItem} from '../../models/account-group-list-item';
import {Router} from '@angular/router';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {AccountsClient} from '../../clients/accounts-client/accounts-client.service';

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
  changeDetection: ChangeDetectionStrategy.Default,
  templateUrl: './account-organizer.component.html',
  styleUrl: './account-organizer.component.scss',
  encapsulation: ViewEncapsulation.None
})

export class AccountOrganizerComponent implements OnChanges{
  @Input() groupName: string = '';
  readonly layoutGroup = input.required<AccountGroupModel>();
  readonly accounts = input.required<AccountModel[]>();
  readonly name = model('');
  private readonly folderNameDialog = inject(MatDialog);
  private readonly deleteFolderConfirmDialog = inject(MatDialog);
  private readonly globalConstants: GlobalConstantsService = inject(GlobalConstantsService);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly accountsClient: AccountsClient = inject(AccountsClient);
  private readonly router: Router = inject(Router);
  protected readonly isFolder = isFolder;
  protected readonly isAccount = isAccount;
  protected readonly isDraggable = isDraggable;

  layoutChanged = output<AccountGroupModel>();

  nodes: NodeType[] = [];
  rootNode: FolderNode = {
    type: 'folder',
    name: 'Loading...',
    id: uuidv7(),
    isDraggable: false,
    children: new Array<NodeType>(),
    isExpanded: false,
    folderBalance: 0
  }

  dropTargetIds: string[] = [];
  dropActionTodo: DropInfo | null = null;

  @ViewChild(MatMenuTrigger)
  contextMenu!: MatMenuTrigger;

  contextMenuPosition = { x: '0px', y: '0px' };

  constructor(
    @Inject(DOCUMENT) private document: Document,
    private changeDetectorRef: ChangeDetectorRef) {
  }

  //#region Context Menu
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
          isExpanded: false,
          folderBalance: 0
        }
        item.children.push(newFolder);
        item.isExpanded = true;
        this.changeDetectorRef.detectChanges();
        this.notifyLayoutChanged();
      }
    })
  }

  onContextMenuRename(item: FolderNode) {
    const dialogRef = this.folderNameDialog.open(GetFolderName, {
      data: {name: item.name}
    });
    dialogRef.afterClosed().subscribe(result => {
      if (result) {
        item.name = result;
        this.changeDetectorRef.detectChanges();
        this.notifyLayoutChanged();
      }
    })
  }

  onContextMenuDelete(item: FolderNode) {
    const dialogRef = this.deleteFolderConfirmDialog.open(DeleteFolderConfirm, {
      data: {confirm: false},
      autoFocus: 'dialog'
    });
    dialogRef.afterClosed().subscribe(result => {
      if (result) {
        const container = this.getContainingFolder(item.id);
        if (item.children.length == 0) {
          if (container) {
            container[0].children.splice(container[1], 1);
            this.changeDetectorRef.detectChanges();
            this.notifyLayoutChanged();
          }
        }
        else {
          const children = item.children.splice(0, item.children.length);
          container?.[0].children.splice(container[1], 1);
          container?.[0].children.push(...children);
          this.changeDetectorRef.detectChanges();
          this.notifyLayoutChanged();
        }
      }
    })
  }
  //#endregion

  accountClicked(id: string) {
    this.userData.set(this.globalConstants.accountIdKey, id);
    void this.router.navigate(['/account']);
  }

  ngOnChanges(changes: SimpleChanges) {
    if (!changes['layoutGroup'].firstChange && !changes['accounts'].firstChange) {
      this.buildTree();
      this.prepareDragDrop(this.nodes.filter(n => n.type == 'folder') as FolderNode[]);
    }
    if (this.nodes.length > 0) {
      this.getBalances();
    }
  }

  //#region Drag and Drop
  prepareDragDrop(nodes: FolderNode[]) {
    nodes.forEach(node => {
      this.dropTargetIds.push(node.id);
      this.prepareDragDrop(node.children.filter(n => n.type == 'folder') as FolderNode[]);
    });
  }

  private buildTree() {
    const layoutGroup = this.layoutGroup();
    const accounts = this.accounts();
    this.rootNode = {
      type: 'folder',
      name: this.groupName,
      id: layoutGroup.id,
      isDraggable: false,
      children: new Array<NodeType>(),
      isExpanded: false,
      folderBalance: 0
    }
    this.nodes.push(this.rootNode);
    if ((layoutGroup.items) || (accounts)) {
      this.addChildren(this.rootNode, layoutGroup);
    }
  }

  private addChildren(parent: FolderNode, group: AccountGroupModel) {
    group.items?.forEach(item => {
      parent.children.push(this.toNodeType(item));
    });
  }

  @debounce(50)
  dragMoved(event: { pointerPosition: { x: number; y: number; }; }) {
    let potentialDestinationContainer = this.document.elementFromPoint(event.pointerPosition.x, event.pointerPosition.y) as HTMLElement;
    if (!potentialDestinationContainer) {
      this.clearDragInfo();
      return;
    }
    if (potentialDestinationContainer.parentElement?.classList.contains("folder-node")) {
      potentialDestinationContainer = potentialDestinationContainer.parentElement;
    }
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
    this.showDragInfo();
  }

  drop(event: { item: { data: any; }; previousContainer: { id: any; }; container: { data: any; }; }) {
    try {
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
      this.notifyLayoutChanged();
      this.updateBalances();
    }
    finally {
      this.clearDragInfo(true);
    }
  }

  notifyLayoutChanged() {
    this.layoutChanged.emit(this.toAccountGroupModel());
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

  showDragInfo() {
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

  trackByFn(index: number, item: NodeType) {
    return item.id;
  }
  //#endregion

  //#region Initialize Balances
  private getBalances(searchNode: FolderNode = this.nodes[0] as FolderNode): void {
    const accountBalancePromises: Promise<void>[] = [];

    searchNode.children.forEach(node => {
      if (isAccount(node)) {
        const account = this.accounts().find(a => a.id === (node as AccountNode).id);
        if (account) {
          const balancePromise = new Promise<void>((resolve) => {
            this.accountsClient.getBalanceOnDate(account.id, new Date()).subscribe(balance => {
              node.balance = balance;
              this.propagateChange(node); // Update folder balances
              resolve();
            });
          });
          accountBalancePromises.push(balancePromise);
        }
      }

      if (isFolder(node)) {
        this.getBalances(node as FolderNode); // Recursively process folder children
      }
    });

    // Wait for all account balances to be fetched before updating the UI
    Promise.all(accountBalancePromises).then(() => {
      this.changeDetectorRef.detectChanges();
    });
  }

  private propagateChange(node: NodeType) {
    let container = this.getContainingFolder(node.id);
    if (container?.at(0)) {
      const folder = container[0];
      if (folder) {
        if ("balance" in node) {
          folder.folderBalance += node.balance;
        }
        if ("folderBalance" in node) {
          folder.folderBalance += node.folderBalance;
        }
        this.propagateChange(folder);
      }
    }
  }
  //#endregion

  //#region Update Balances
  private recalculateBalances(node: FolderNode): number {
    // Reset the folder balance
    node.folderBalance = 0;

    // Iterate through children to calculate the total balance
    node.children.forEach(child => {
      if (this.isAccount(child)) {
        node.folderBalance += child.balance || 0; // Add account balance
      } else if (this.isFolder(child)) {
        node.folderBalance += this.recalculateBalances(child); // Recursively calculate folder balance
      }
    });

    return node.folderBalance; // Return the calculated balance
  }

  // Call this method after any structural change
  private updateBalances(): void {
    this.recalculateBalances(this.rootNode); // Start from the root node
    this.changeDetectorRef.detectChanges(); // Update the UI
  }
  //#endregion

  //#region Helper Methods
  toAccountGroupModel(): AccountGroupModel {
    const accountGroupListItem = this.fromNodeType(this.rootNode);
    const accountGroupModel = new AccountGroupModel(this.groupName);
    accountGroupModel.id = accountGroupListItem.accountGroup!.id;
    accountGroupModel.items = accountGroupListItem.accountGroup!.items;
    return accountGroupModel;
  }

  toNodeType(item: AccountGroupListItem): NodeType {
    switch (item.type) {
      case AccountGroupListItemType.group:
        return {
          id: item.accountGroup!.id,
          name: item.accountGroup!.name,
          type: 'folder',
          children: item.accountGroup!.items.map((child) => this.toNodeType(child)),
          isDraggable: true,
          isExpanded: false,
          folderBalance: 0
        };
      case AccountGroupListItemType.account:
        const account = this.accounts().find(a => a.id == item.accountId);
        return {
          id: item.accountId!,
          name: account?.info?.name ?? '',
          type: 'account',
          balance: 0
        };
    }
  }

  fromNodeType(node: NodeType): AccountGroupListItem {
    if (node.type === 'folder') {
      const group = new AccountGroupListItem();
      group.type = AccountGroupListItemType.group;
      group.accountId = null;
      group.accountGroup = new AccountGroupModel(node.name);
      group.accountGroup.id = node.id;
      group.accountGroup.items = (node as FolderNode).children.map((child) => this.fromNodeType(child));
      return group;
    } else {
      const account = new AccountGroupListItem();
      account.type = AccountGroupListItemType.account;
      account.accountId = node.id;
      account.accountGroup = null;
      return account;
    }
  }
  //#endregion
}
