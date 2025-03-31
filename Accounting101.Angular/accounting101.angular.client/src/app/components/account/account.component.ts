import {Component, inject, output} from '@angular/core';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {AccountHeaderComponent} from '../../controls/account-header/account-header.component';
import {MenuComponent} from '../menu/menu.component';
import {Screen} from '../../enums/screen.enum';
import {TransactionListComponent} from '../../controls/transaction-list/transaction-list.component';
import {ClientHeaderComponent} from '../../controls/client-header/client-header.component';
import {ClientClient} from '../../clients/client-client/client-client.service';
import {AsyncPipe} from '@angular/common';
import {MatDivider} from '@angular/material/divider';
import {FastEntryComponent} from '../../controls/fast-entry/fast-entry.component';
import {AccountsClient} from '../../clients/accounts-client/accounts-client.service';
import {AccountModel} from '../../models/account.model';

@Component({
  selector: 'app-account',
  imports: [
    AccountHeaderComponent,
    MenuComponent,
    TransactionListComponent,
    ClientHeaderComponent,
    AsyncPipe,
    MatDivider,
    FastEntryComponent
  ],
  templateUrl: './account.component.html',
  styleUrl: './account.component.scss'
})

export class AccountComponent {
  updateTransactions = false;
  account: AccountModel = new AccountModel();
  private readonly globals = inject(GlobalConstantsService);
  private readonly userDataService = inject(UserDataService);
  private readonly clientManager = inject(ClientClient);
  private readonly accountsManager = inject(AccountsClient);
  readonly client = this.clientManager.getClient(this.userDataService.get(this.globals.clientIdKey));
  readonly accounts = this.accountsManager.getAccounts();
  filteredAccounts: AccountModel[] = [];
  allAccounts: AccountModel[] = [];

  protected readonly Screen = Screen;

  constructor() {
    const accountId = this.userDataService.get(this.globals.accountIdKey);
    this.accounts.subscribe(accts => {
      this.allAccounts = accts;
      this.account = accts.find(a => a.id === accountId)!;
      this.filteredAccounts = accts.filter(a => a.id !== accountId);
    });
  }

  transactionsUpdated() {
    this.updateTransactions = true;
    this.updateTransactions = false;
  }
}
