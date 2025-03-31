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
    NgIf
  ],
  templateUrl: './transaction-list.component.html',
  styleUrl: './transaction-list.component.scss'
})

export class TransactionListComponent implements OnChanges{
  readonly accountId = input.required<string>();
  readonly accounts = input.required<AccountModel[]>();
  readonly transactionsUpdated = input<boolean>();
  private readonly accountsManager = inject(AccountsClient);
  private readonly displayLines: TransactionDisplayLine[] = [];
  private transactions: TransactionModel[] = [];
  data = new MatTableDataSource<TransactionModel>();
  columnsToDisplay = ['when', 'amount'];

  ngOnChanges(changes:SimpleChanges) {
    if (changes['accountId'] && changes['accounts']) {
      this.accountsManager.getTransactionsForAccount(this.accountId())
        .subscribe(transactions => {
          this.transactions = transactions;
          this.data.data = this.transactions;
          this.data.paginator = null;
          const minDate = new Date(Math.min(...this.transactions.map(t => new Date(t.when).getTime())));
          this.accountsManager.getBalanceOnDate(this.accountId(), minDate).subscribe(startBalance => {
            // sort transactions by date from oldest to newest
            this.transactions.sort((a, b) => new Date(a.when).getTime() - new Date(b.when).getTime());
            this.transactions.forEach(t => {
              
            });
          });
        });
    }
  }

  buildDisplayLines() {

  }
}
