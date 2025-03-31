import {Component, inject, input, OnChanges, SimpleChanges} from '@angular/core';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
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
import {NgIf} from '@angular/common';
import {AccountModel} from '../../models/account.model';
import {TransactionDisplayLine} from '../../models/transaction-display-line.model.cs';
import {RouterLink} from '@angular/router';

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
    RouterLink
  ],
  templateUrl: './transaction-list.component.html',
  styleUrl: './transaction-list.component.scss'
})

export class TransactionListComponent implements OnChanges{
  readonly account = input.required<AccountModel>();
  readonly accounts = input.required<AccountModel[]>();
  readonly transactionsUpdated = input<boolean>();
  private readonly accountsManager = inject(AccountsClient);
  private transactions: TransactionModel[] = [];
  data = new MatTableDataSource<TransactionDisplayLine>();
  columnsToDisplay = ['when', 'debit', 'credit', 'balance', 'otherAccount'];

  ngOnChanges(changes:SimpleChanges) {
    if (!changes['account'].firstChange || !changes['accounts'].firstChange) {
      this.accountsManager.getTransactionsForAccount(this.account().id)
        .subscribe(transactions => {
          this.transactions = transactions;
          this.data.paginator = null;
          const minDate = new Date(Math.min(...this.transactions.map(t => new Date(t.when).getTime())));
          this.accountsManager.getBalanceOnDate(this.account().id, minDate).subscribe(startBalance => {
            // sort transactions by date from oldest to newest
            this.transactions.sort((a, b) => new Date(a.when).getTime() - new Date(b.when).getTime());
            this.transactions.forEach(t => {
              const balanceAfterTransaction = this.calculateBalanceAfterTransaction(startBalance, t);
              const displayLine = new TransactionDisplayLine();
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
}
