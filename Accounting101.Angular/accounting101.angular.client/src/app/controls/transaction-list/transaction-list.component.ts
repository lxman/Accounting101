import {Component, inject} from '@angular/core';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {Transaction} from '../../models/transaction.model';
import {AccountsManagerService} from '../../services/accounts-manager/accounts-manager.service';

@Component({
  selector: 'app-transaction-list',
  imports: [],
  templateUrl: './transaction-list.component.html',
  styleUrl: './transaction-list.component.scss'
})
export class TransactionListComponent {
  private readonly accountsManager = inject(AccountsManagerService);
  private readonly globals = inject(GlobalConstantsService);
  private readonly userData = inject(UserDataService);
  private transactions: Transaction[] = [];

  constructor() {
    this.accountsManager.transactionsForAccount(this.userData.get(this.globals.accountIdKey))
      .subscribe(transactions => {
        this.transactions = transactions;
        console.log("Transactions retrieved.");
      });
  }
}
