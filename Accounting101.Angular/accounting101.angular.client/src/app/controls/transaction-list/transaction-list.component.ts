import {
  ChangeDetectorRef,
  Component,
  inject,
  input,
  OnChanges,
  OnDestroy,
  OnInit,
  output,
  SimpleChanges,
  ViewChild,
  NgZone
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
import {Subscription} from 'rxjs';
import {StreamedTransactionModel} from '../../models/streamed-transaction.model';

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
export class TransactionListComponent implements OnChanges, OnDestroy, OnInit {
  @ViewChild(MatMenuTrigger) trigger!: MatMenuTrigger;
  contextMenuPosition = { x: '0px', y: '0px' };

  readonly account = input.required<AccountModel>();
  readonly accounts = input.required<AccountModel[]>();
  readonly linkWasClicked = output<string>();
  private readonly accountsManager = inject(AccountsClient);
  private messageService = inject(MessageService);
  private zone = inject(NgZone);
  private transactionStreamSubscription?: Subscription;

  subscription = this.messageService.message$.subscribe((message: Message<string>) => {
    if (message.type === 'string' && message.destination === 'app-transaction-list') {
      if (message.message === 'update') {
        this.initializeWithStreaming();
      }
      if (message.message === 'editing?') {
        const message = new Message<boolean>();
        message.source = 'app-transaction-list';
        message.destination = 'app-fast-entry';
        message.type = 'boolean';
        message.message = this.data.data.some(e => e.selected);
        this.messageService.sendMessage(message);
      }
    }
  });

  private changeDetector = inject(ChangeDetectorRef);
  private transactions: StreamedTransactionModel[] = [];
  data = new MatTableDataSource<TransactionDisplayLine>();
  columnsToDisplay = ['when', 'debit', 'credit', 'balance', 'otherAccount'];
  isStreaming = false;

  ngOnInit() {
    // Only initialize if we have a valid account
    if (this.account() && this.account().id && this.account().id !== '00000000-0000-0000-0000-000000000000') {
            this.initializeWithStreaming();
    } else {
          }
  }

  ngOnChanges(changes: SimpleChanges) {
    if (changes['account'] || changes['accounts']) {
      // Only initialize if we have a valid account
      if (this.account() && this.account().id && this.account().id !== '00000000-0000-0000-0000-000000000000') {
                this.initializeWithStreaming();
      } else {
              }
    }
  }

  initializeWithStreaming() {
    if (this.transactionStreamSubscription) {
      this.transactionStreamSubscription.unsubscribe();
    }

    this.data.data = [];
    this.transactions = [];
    this.data.paginator = null;

    // Check if account ID is valid (not blank/default GUID)
    if (!this.account() || !this.account().id || this.account().id === '00000000-0000-0000-0000-000000000000') {
            return; // Don't start streaming with invalid account ID
    }

    this.isStreaming = true;

    this.transactionStreamSubscription = this.accountsManager
      .streamTransactionsForAccount(this.account().id)
      .subscribe({
        next: (transactions) => {
                    this.zone.run(() => {
            this.transactions = transactions;
            this.processTransactions(this.transactions);
            this.changeDetector.detectChanges();
          });
        },
        error: (err) => {
          console.error('Error streaming transactions:', err);
          this.isStreaming = false;
                    this.initialize();
          this.changeDetector.detectChanges();
        },
        complete: () => {
                    this.isStreaming = false;
          this.changeDetector.detectChanges();
        }
      });
  }

  initialize() {
    this.data.data = [];
    this.transactions = [];
    this.data.paginator = null;


    this.accountsManager.getTransactionsForAccount(this.account().id).subscribe({
      next: (transactions) => {
                this.zone.run(() => {
          this.processTransactions(transactions);
          this.changeDetector.detectChanges();
        });
      },
      error: (err) => {
        console.error('Error fetching transactions:', err);
      }
    });
  }

  processTransactions(transactions: StreamedTransactionModel[]) {

    if (transactions.length === 0) {
            this.data.data = []; // Ensure data is empty
      return;
    }

    // Debug the first transaction to understand its structure

    // Create an account lookup map for faster lookups
    const accountLookup = new Map<string, AccountModel>();
    if (this.accounts() && this.accounts().length > 0) {
      this.accounts().forEach(a => {
                accountLookup.set(a.id, a);
      });
          } else {
      console.warn('No accounts available for lookup');
    }

    let startBalance = this.account().startBalance;

    // Clear existing display data
    this.data.data = [];

    // Sort transactions by date - only necessary on initial load or if new transactions arrive out of order
    if (!this.isStreaming || transactions.length === this.transactions.length) {
      transactions.sort((a, b) => new Date(a.When).getTime() - new Date(b.When).getTime());
          }

    // Process all transactions
    const displayLines: TransactionDisplayLine[] = [];

    transactions.forEach(t => {
      const balanceAfterTransaction = this.calculateBalanceAfterTransaction(startBalance, t);
      const displayLine = new TransactionDisplayLine();
      displayLine.id = t.Id;

      // Format the date properly
      try {
        if (t.When) {
          // Try to parse and format the date
          const date = new Date(t.When);
          displayLine.when = date.toLocaleDateString();
        } else {
          displayLine.when = 'Unknown date';
        }
      } catch (e) {
        console.error('Error formatting date:', t.When, e);
        displayLine.when = String(t.When);
      }

      displayLine.debit = t.DebitedAccountId === this.account().id ? t.Amount : null;
      displayLine.credit = t.CreditedAccountId === this.account().id ? t.Amount : null;
      displayLine.balance = balanceAfterTransaction;

      // Find the other account more efficiently using the map
      const otherAccountId = t.DebitedAccountId === this.account().id
        ? t.CreditedAccountId
        : t.DebitedAccountId;


      // Try to find the account in the lookup
      const otherAccount = accountLookup.get(otherAccountId);
      if (otherAccount && otherAccount.info) {
        displayLine.otherAccount = otherAccount.info.coAId + " " + otherAccount.info.name;
              } else {
        console.warn('Could not find other account with ID:', otherAccountId);
        // Provide a fallback display name with the ID we have
        displayLine.otherAccount = 'Unknown Account (' + otherAccountId?.substring(0, 8) + '...)';
      }

      startBalance = balanceAfterTransaction;
      displayLines.push(displayLine);
    });

    // Update the data source all at once (more efficient than pushing one by one)
    this.data.data = displayLines;

    // Ensure change detection runs
    this.changeDetector.detectChanges();
  }

  calculateBalanceAfterTransaction(balanceBefore: number, t: StreamedTransactionModel): number {
    if (t.DebitedAccountId === this.account().id && this.account().isDebitAccount) {
      return balanceBefore + t.Amount;
    }
    if (t.CreditedAccountId === this.account().id && !this.account().isDebitAccount) {
      return balanceBefore + t.Amount;
    }
    if (t.DebitedAccountId === this.account().id && !this.account().isDebitAccount) {
      return balanceBefore - t.Amount;
    }
    if (t.CreditedAccountId === this.account().id && this.account().isDebitAccount) {
      return balanceBefore - t.Amount;
    }
    return balanceBefore;
  }

  linkClicked(element: TransactionDisplayLine) {
    const otherAccount = element.otherAccount;
    this.linkWasClicked.emit(otherAccount);
  }

  rowClicked($event: MouseEvent, element: TransactionDisplayLine) {
    if ($event.button === 0) {
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
      const transactions = this.transactions.filter(t => t.Id === element.id);

      const message = new Message<TransactionModel[]>();
      message.source = 'app-transaction-list';
      message.destination = 'app-fast-entry';
      message.message = this.toTransactionModelArray(transactions)
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
        this.initializeWithStreaming();
        this.changeDetector.detectChanges();
      }
    });
  }

  ngOnDestroy() {
    this.subscription.unsubscribe();
    if (this.transactionStreamSubscription) {
      this.transactionStreamSubscription.unsubscribe();
    }
  }

  private toTransactionModelArray(transaction: StreamedTransactionModel[]): TransactionModel[] {
    const transactionModelArray: TransactionModel[] = [];
    transaction.forEach(t => {
      const transactionModel = this.toTransactionModel(t);
      transactionModelArray.push(transactionModel);
    });
    return transactionModelArray;
  }

  private toTransactionModel(transaction: StreamedTransactionModel): TransactionModel {
    const transactionModel = new TransactionModel();
    transactionModel.id = transaction.Id;
    transactionModel.debitedAccountId = transaction.DebitedAccountId;
    transactionModel.creditedAccountId = transaction.CreditedAccountId;
    transactionModel.amount = transaction.Amount;
    transactionModel.when = new Date(transaction.When).toLocaleDateString();
    return transactionModel;
  }
}
