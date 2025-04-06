import {
  ChangeDetectorRef,
  Component,
  inject,
  input,
  OnChanges,
  OnDestroy,
  output,
  SimpleChanges,
  ViewChild
} from '@angular/core';
import {TransactionModel} from '../../models/transaction.model';
import {AccountsClient} from '../../clients/accounts-client/accounts-client.service';
import {
  MatCell,
  MatCellDef,
  MatHeaderCell,
  MatHeaderCellDef,
  MatHeaderRow,
  MatHeaderRowDef,
  MatRow,
  MatRowDef,
  MatTable,
  MatTableModule,
  MatTableDataSource
} from '@angular/material/table';
import {TypeSafeMatCellDef} from '../../directives/type-safe-mat-cell-def.directive';
import {TypeSafeMatRowDef} from '../../directives/type-safe-mat-row-def.directive';
import {NgClass, NgIf} from '@angular/common';
import {AccountModel} from '../../models/account.model';
import {TransactionDisplayLine} from '../../models/transaction-display-line.model.cs';
import {RouterLink} from '@angular/router';
import {MessageService} from '../../services/message/message.service';
import {Message} from '../../models/message.model';
import {MatMenu, MatMenuContent, MatMenuItem, MatMenuTrigger} from '@angular/material/menu';

@Component({
  selector: 'app-transaction-list',
  imports: [
    MatTable,
    MatTableModule,
    MatHeaderCell,
    MatHeaderCellDef,
    MatCell,
    MatCellDef,
    MatHeaderRow,
    MatHeaderRowDef,
    MatRow,
    MatRowDef,
    TypeSafeMatCellDef,
    TypeSafeMatRowDef,
    NgIf,
    RouterLink,
    NgClass,
    MatMenu,
    MatMenuTrigger,
    MatMenuContent,
    MatMenuItem
  ],
  templateUrl: './transaction-list.component.html',
  styleUrl: './transaction-list.component.scss'
})

export class TransactionListComponent implements OnChanges, OnDestroy{
  @ViewChild(MatMenuTrigger) trigger!: MatMenuTrigger;
  contextMenuPosition = { x: '0px', y: '0px' };

  readonly account = input.required<AccountModel>();
  readonly accounts = input.required<AccountModel[]>();
  readonly linkWasClicked = output<string>();
  private readonly accountsManager = inject(AccountsClient);
  private messageService = inject(MessageService);
  subscription = this.messageService.message$.subscribe((message: Message<string>) => {
    if (message.type === 'string' && message.destination === 'app-transaction-list') {
      if (message.message === 'update') {
        this.initialize();
      }
      if (message.message === 'editing?') {
        const message = new Message<boolean>();
        message.source = 'app-transaction-list';
        message.destination = 'app-fast-entry';
        message.type = 'TransactionDisplayLine';
        message.message = this.data.data.some(e => e.selected);
        this.messageService.sendMessage(message);
      }
    }
  });
  private changeDetector = inject(ChangeDetectorRef);
  private transactions: TransactionModel[] = [];
  data = new MatTableDataSource<TransactionDisplayLine>();
  columnsToDisplay = ['when', 'debit', 'credit', 'balance', 'otherAccount'];

  ngOnChanges(changes:SimpleChanges) {
    if (!changes['account'].firstChange || !changes['accounts'].firstChange) {
      this.initialize();
    }
  }

  initialize() {
    this.data.data = [];
    this.transactions = [];
    this.data.paginator = null;
    this.accountsManager.getTransactionsForAccount(this.account().id)
      .subscribe(transactions => {
        this.data.data = [];
        this.transactions = transactions;
        this.data.paginator = null;
        const minDate = new Date(Math.min(...this.transactions.map(t => new Date(t.when).getTime())));
        this.accountsManager.getBalanceOnDate(this.account().id, minDate).subscribe(startBalance => {
          // sort transactions by date from oldest to newest
          this.transactions.sort((a, b) => new Date(a.when).getTime() - new Date(b.when).getTime());
          this.transactions.forEach(t => {
            const balanceAfterTransaction = this.calculateBalanceAfterTransaction(startBalance, t);
            const displayLine = new TransactionDisplayLine();
            displayLine.id = t.id;
            displayLine.when = t.when;
            displayLine.debit = t.debitedAccountId === this.account().id ? t.amount : null;
            displayLine.credit = t.creditedAccountId === this.account().id ? t.amount : null;
            displayLine.balance = balanceAfterTransaction;
            if (t.debitedAccountId !== this.account().id) {
              const otherAccount = this.accounts().find(a => a.id === t.debitedAccountId);
              if (otherAccount) {
                displayLine.otherAccount = otherAccount.info.coAId + " " + otherAccount.info.name;
              }
            } else {
              const otherAccount = this.accounts().find(a => a.id === t.creditedAccountId);
              if (otherAccount) {
                displayLine.otherAccount = otherAccount.info.coAId + " " + otherAccount.info.name;
              }
            }
            startBalance = balanceAfterTransaction;
            this.data.data.push(displayLine);
          });
        });
      });
  }

  calculateBalanceAfterTransaction(balanceBefore: number, t: TransactionModel): number {
    if (t.debitedAccountId === this.account().id && this.account().isDebitAccount) {
      return balanceBefore + t.amount;
    }
    if (t.creditedAccountId === this.account().id && !this.account().isDebitAccount) {
      return balanceBefore + t.amount;
    }
    if (t.debitedAccountId === this.account().id && !this.account().isDebitAccount) {
      return balanceBefore - t.amount;
    }
    if (t.creditedAccountId === this.account().id && this.account().isDebitAccount) {
      return balanceBefore - t.amount;
    }
    return balanceBefore;
  }

  linkClicked(element: TransactionDisplayLine) {
    const otherAccount = element.otherAccount;
    this.linkWasClicked.emit(otherAccount);
  }

  rowClicked($event: MouseEvent, element: TransactionDisplayLine) {
    if ($event.button === 0) {
      // left click
      const dataElement = this.data.data.find(e => e.id === element.id);
      if (dataElement?.selected) {
        dataElement.selected = false;
        const message = new Message<string>();
        message.source = 'app-transaction-list';
        message.destination = 'app-fast-entry';
        message.message = 'clear';
        message.type = 'string';
        this.messageService.sendMessage(message);
        return;
      } else {
        this.data.data.forEach(e => e.selected = false);
        dataElement!.selected = true;
      }
      const message = new Message<TransactionModel>();
      message.source = 'app-transaction-list';
      message.destination = 'app-fast-entry';
      message.message = this.transactions.find(t => t.id === element.id)!;
      message.type = 'TransactionModel';
      this.messageService.sendMessage(message);
      this.changeDetector.detectChanges();
    }
  }

  onRightClick($event: MouseEvent, element: TransactionDisplayLine) {
    $event.preventDefault();
    $event.stopPropagation();
    this.contextMenuPosition.x = $event.clientX + 'px';
    this.contextMenuPosition.y = $event.clientY + 'px';
    this.trigger.menuData = {
      x: $event.clientX,
      y: $event.clientY,
      item: element
    }
    this.trigger.openMenu();
  }

  onContextMenuDelete(element: TransactionDisplayLine) {
    this.accountsManager.deleteTransaction(element.id).subscribe(success => {
      if (success) {
        this.initialize();
        this.changeDetector.detectChanges();
      }
    });
  }

  ngOnDestroy() {
    this.subscription.unsubscribe();
  }
}
