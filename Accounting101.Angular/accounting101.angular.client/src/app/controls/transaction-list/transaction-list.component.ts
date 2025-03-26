import {Component, inject} from '@angular/core';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {TransactionModel} from '../../models/transaction.model';
import {AccountsManagerService} from '../../services/accounts-manager/accounts-manager.service';
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
  MatTableDataSource
} from '@angular/material/table';
import {TypeSafeMatCellDef} from '../../directives/type-safe-mat-cell-def.directive';
import {TypeSafeMatRowDef} from '../../directives/type-safe-mat-row-def.directive';
import {NgIf} from '@angular/common';

@Component({
  selector: 'app-transaction-list',
  imports: [
    MatTable,
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

export class TransactionListComponent {
  private readonly accountsManager = inject(AccountsManagerService);
  private readonly globals = inject(GlobalConstantsService);
  private readonly userData = inject(UserDataService);
  private transactions: TransactionModel[] = [];
  data = new MatTableDataSource<TransactionModel>();
  columnsToDisplay = ['when', 'amount', 'creditAccountId', 'debitAccountId'];

  constructor() {
    this.accountsManager.transactionsForAccount(this.userData.get(this.globals.accountIdKey))
      .subscribe(transactions => {
        this.transactions = transactions;
        this.data.data = this.transactions;
        this.data.paginator = null;
      });
  }
}
